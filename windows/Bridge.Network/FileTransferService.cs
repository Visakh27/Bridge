using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bridge.Protocol;
using Bridge.Security;

namespace Bridge.Network;

public enum TransferDirection { Sending, Receiving }

/// <summary>
/// <see cref="Interrupted"/>: the connection dropped; the transfer is kept and
/// continues from where it stopped when the devices reconnect (both sides
/// advertise <c>files.resume</c>).
/// </summary>
public enum TransferStatus { InProgress, Completed, Failed, Cancelled, Interrupted }

public sealed class TransferProgress
{
    public required string TransferId { get; init; }
    public required string FileName { get; init; }
    public required TransferDirection Direction { get; init; }
    public TransferStatus Status { get; set; } = TransferStatus.InProgress;
    public int ChunksDone { get; set; }
    public int TotalChunks { get; set; }
    /// <summary>For completed incoming transfers, where the file was saved.</summary>
    public string? SavedPath { get; set; }
    /// <summary>Folder transfers: the file's folder inside the batch ('/'-separated), and its place in the batch.</summary>
    public string? RelativePath { get; init; }
    public string? BatchId { get; init; }
    public int? BatchIndex { get; init; }
    public int? BatchCount { get; init; }
    /// <summary>Chunk the transfer (re)started from: &gt; 0 when it was resumed.</summary>
    public int ResumedFrom { get; set; }
    /// <summary>Sending: the local file being sent (for "Retry").</summary>
    public string? SourcePath { get; init; }
    public double Fraction => TotalChunks == 0 ? (Status == TransferStatus.Completed ? 1 : 0) : (double)ChunksDone / TotalChunks;
}

/// <param name="PeerId">The paired device on the other end (keys resumable state).</param>
/// <param name="PeerSupportsResume">Peer advertised <c>files.resume</c>.</param>
/// <param name="PeerSupportsFolders">Peer advertised <c>files.folders</c>.</param>
public sealed record SendOptions(string PeerId, bool PeerSupportsResume, bool PeerSupportsFolders);

/// <summary>
/// Chunked, integrity-checked file transfer (protocol/protocol.md). Chunk size
/// chosen to stay comfortably under typical socket buffers once
/// base64-encoded (64 KB raw -> ~87 KB encoded).
///
/// Receiving: names and folder paths are sanitised (no traversal, no
/// overwriting), chunks must arrive in order and never exceed the announced
/// size. Resumable transfers keep their partial file (in a hidden
/// ".bridge-partial" folder) across disconnects for 24 h.
/// Sending: resumable sends stay in an outbox until the peer acknowledges them
/// and are offered again on the next connection (<see cref="ResumePendingAsync"/>).
/// </summary>
public sealed class FileTransferService
{
    public const int ChunkSize = 64 * 1024;
    public const int MaxConcurrentIncoming = 8;
    public const int MaxFolderFiles = 10_000;
    public const string PartialDirName = ".bridge-partial";
    /// <summary>Abandoned resumable partials kept per peer; older ones are evicted.</summary>
    public const int MaxPartialsPerPeer = 32;
    public static readonly TimeSpan ResumeReplyTimeout = TimeSpan.FromSeconds(30);

    private sealed class Incoming
    {
        public required EncryptedChannel Channel { get; init; }
        public required string PeerId { get; init; }
        public required FileStartPayload Meta { get; init; }
        public required string SafeName { get; init; }
        public required string TempPath { get; init; }
        public string? MetaPath { get; init; }             // resumable only
        public required FileStream Stream { get; init; }
        public int NextIndex { get; set; }
        public long BytesWritten { get; set; }
        public int ResumedFrom { get; init; }
        public bool Resumable => MetaPath is not null;
    }

    /// <summary>
    /// An outgoing transfer, kept from file.start until the receiver's file.ack.
    /// The receiver may ack early (rejecting file.start, or failing mid-transfer);
    /// that verdict stops the sender instead of being ignored.
    /// </summary>
    private sealed class Outgoing
    {
        public required EncryptedChannel Channel { get; init; }
        public required TransferProgress Progress { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        /// <summary>file.resume's nextIndex; −1 = the receiver already has the whole file (ack ok).</summary>
        public TaskCompletionSource<int> ResumeReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Resumable sends: the receiver's file.ack after file.end (null = connection lost).</summary>
        public TaskCompletionSource<string> FinalAck { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool AwaitingFinalAck { get; set; } // file.end is (about to be) sent
        public string? PeerVerdict { get; set; }   // non-ok file.ack status, if any
        public bool Finished { get; set; }         // sender loop has returned
        public bool Disconnected { get; set; }     // stopped because the session ended (resumable)
    }

    private readonly ConcurrentDictionary<string, Outgoing> _outgoing = new();
    private readonly Dictionary<string, Incoming> _incoming = new(); // only touched by a session's receive loop
    private readonly object _incomingGate = new();
    private readonly TimeProvider _time;

    public TransferStateStore State { get; }

    public event Action<TransferProgress>? ProgressChanged;

    /// <param name="state">Persistent resume state; in-memory only when null.</param>
    public FileTransferService(TransferStateStore? state = null, TimeProvider? time = null)
    {
        State = state ?? new TransferStateStore();
        _time = time ?? TimeProvider.System;
    }

    // ------------------------------------------------------------------ sending

    /// <summary>Sends one file without resume or folder support (legacy peers, tests).</summary>
    public Task<TransferProgress> SendFileAsync(EncryptedChannel channel, string filePath, CancellationToken ct = default) =>
        SendFileAsync(channel, filePath, new SendOptions(channel.PeerDeviceId, false, false), ct);

    /// <summary>Sends a file; never throws for I/O or cancellation (the returned status says what happened).</summary>
    public Task<TransferProgress> SendFileAsync(EncryptedChannel channel, string filePath, SendOptions options, CancellationToken ct = default)
    {
        var entry = new TransferStateStore.OutboxEntry(Guid.NewGuid().ToString(), options.PeerId, Path.GetFullPath(filePath),
            Path.GetFileName(filePath), null, null, null, null, _time.GetUtcNow());
        return SendEntryAsync(channel, entry, options, ct);
    }

    /// <summary>
    /// Sends every file under <paramref name="folderPath"/> as one batch, keeping the
    /// folder structure (when the peer supports folders; otherwise as loose files).
    /// Symbolic links and junctions are skipped. Returns one result per file.
    /// </summary>
    public async Task<IReadOnlyList<TransferProgress>> SendFolderAsync(EncryptedChannel channel, string folderPath, SendOptions options, CancellationToken ct = default)
    {
        var root = new DirectoryInfo(Path.GetFullPath(folderPath));
        var files = EnumerateFiles(root).Take(MaxFolderFiles + 1).ToList();
        if (files.Count > MaxFolderFiles) throw new IOException($"Folder has more than {MaxFolderFiles} files");

        // The top folder's name, even for a drive root ("D:\" → "D_").
        var rootName = SafeFileName.Sanitize(root.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':'));
        string RelativeOf(FileInfo f)
        {
            var sub = Path.GetRelativePath(root.FullName, f.DirectoryName!).Replace('\\', '/');
            return sub == "." ? rootName : rootName + "/" + sub;
        }
        var batchId = Guid.NewGuid().ToString();
        TransferStateStore.OutboxEntry Entry(int i) => new(Guid.NewGuid().ToString(), options.PeerId, files[i].FullName, files[i].Name,
            options.PeerSupportsFolders ? RelativeOf(files[i]) : null, options.PeerSupportsFolders ? batchId : null,
            options.PeerSupportsFolders ? i : null, options.PeerSupportsFolders ? files.Count : null, _time.GetUtcNow());

        var results = new List<TransferProgress>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var result = await SendEntryAsync(channel, Entry(i), options, ct);
            results.Add(result);
            if (result.Status == TransferStatus.Interrupted || !channel.IsConnected)
            {
                // Connection gone: queue the rest so the whole folder resumes later.
                if (options.PeerSupportsResume) State.AddOutboxRange(Enumerable.Range(i + 1, files.Count - i - 1).Select(Entry));
                break;
            }
            if (result.Status == TransferStatus.Cancelled && ct.IsCancellationRequested) break;
        }
        return results;
    }

    /// <summary>Offers again every unacknowledged send to <paramref name="options"/>.PeerId (call when a session starts).</summary>
    public async Task ResumePendingAsync(EncryptedChannel channel, SendOptions options, CancellationToken ct = default)
    {
        if (!options.PeerSupportsResume) return;
        foreach (var entry in State.PendingFor(options.PeerId).OrderBy(e => e.Created))
        {
            if (ct.IsCancellationRequested || !channel.IsConnected) return;
            if (_outgoing.ContainsKey(entry.TransferId)) continue; // already in flight
            var result = await SendEntryAsync(channel, entry, options, ct);
            if (result.Status == TransferStatus.Interrupted) return; // dropped again: wait for the next connection
        }
    }

    /// <summary>User cancel: stops an active send, or drops a queued one.</summary>
    public void CancelTransfer(string transferId)
    {
        State.RemoveOutbox(transferId);
        if (!_outgoing.TryGetValue(transferId, out var outgoing)) return;
        lock (outgoing)
        {
            if (!outgoing.Finished) outgoing.Cts.Cancel();
        }
    }

    private async Task<TransferProgress> SendEntryAsync(EncryptedChannel channel, TransferStateStore.OutboxEntry entry, SendOptions options, CancellationToken ct)
    {
        var resumable = options.PeerSupportsResume;
        var transferId = entry.TransferId;
        var progress = new TransferProgress
        {
            TransferId = transferId, FileName = entry.FileName, Direction = TransferDirection.Sending, SourcePath = entry.FilePath,
            RelativePath = entry.RelativePath, BatchId = entry.BatchId, BatchIndex = entry.BatchIndex, BatchCount = entry.BatchCount,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var outgoing = new Outgoing { Channel = channel, Progress = progress, Cts = cts };
        _outgoing[transferId] = outgoing;
        if (resumable) State.AddOutbox(entry);

        var started = false;
        var localError = false;
        TransferStatus status;
        try
        {
            string hash;
            long size;
            try
            {
                var info = new FileInfo(entry.FilePath);
                if (!info.Exists) throw new FileNotFoundException("File no longer exists", entry.FilePath);
                size = info.Length;
                var stamp = info.LastWriteTimeUtc;
                if (entry.Sha256 is { } cached && entry.Size == size && entry.LastWriteUtc == stamp)
                {
                    hash = cached; // unchanged since we last hashed it: don't re-read a big file on every reconnect
                }
                else
                {
                    hash = await FileHasher.Sha256HexAsync(entry.FilePath, cts.Token);
                    if (resumable) State.AddOutbox(entry with { Sha256 = hash, Size = size, LastWriteUtc = stamp });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                localError = true; // file deleted / unreadable: nothing to resume
                throw;
            }
            progress.TotalChunks = (int)((size + ChunkSize - 1) / ChunkSize);
            ProgressChanged?.Invoke(progress);

            await channel.SendAsync(BridgeMessage.Create(MessageType.FileStart,
                new FileStartPayload(transferId, entry.FileName, size, hash, progress.TotalChunks)
                {
                    Resumable = resumable ? true : null,
                    RelativePath = entry.RelativePath,
                    BatchId = entry.BatchId,
                    BatchIndex = entry.BatchIndex,
                    BatchCount = entry.BatchCount,
                }), cts.Token);
            started = true;

            var startIndex = 0;
            if (resumable)
            {
                var reply = await outgoing.ResumeReply.Task.WaitAsync(ResumeReplyTimeout, cts.Token);
                if (reply == -1)
                {
                    // The receiver already stored this file (our earlier ack was lost).
                    progress.ChunksDone = progress.TotalChunks;
                    status = TransferStatus.Completed;
                    lock (outgoing) outgoing.Finished = true;
                    State.RemoveOutbox(transferId);
                    _outgoing.TryRemove(transferId, out _);
                    progress.Status = status;
                    ProgressChanged?.Invoke(progress);
                    return progress;
                }
                if (reply < 0 || reply > progress.TotalChunks) throw new InvalidDataException("Bad file.resume index");
                startIndex = reply;
                progress.ResumedFrom = startIndex;
                progress.ChunksDone = startIndex;
            }

            await using var stream = new FileStream(entry.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true);
            stream.Seek((long)startIndex * ChunkSize, SeekOrigin.Begin);
            var buffer = new byte[ChunkSize];
            for (var index = startIndex; index < progress.TotalChunks; index++)
            {
                int read;
                try { read = await ReadFullAsync(stream, buffer, cts.Token); }
                catch (IOException) { localError = true; throw; }
                if (read == 0) { localError = true; throw new IOException("File shrank while it was being sent"); }
                await channel.SendAsync(BridgeMessage.Create(MessageType.FileChunk,
                    new FileChunkPayload(transferId, index, Convert.ToBase64String(buffer, 0, read))), cts.Token);

                progress.ChunksDone = index + 1;
                ProgressChanged?.Invoke(progress);
            }

            lock (outgoing) outgoing.AwaitingFinalAck = true; // the ack can arrive before SendAsync returns
            await channel.SendAsync(BridgeMessage.Create(MessageType.FileEnd, new FileEndPayload(transferId)), cts.Token);
            if (resumable)
            {
                // Only report "sent" once the receiver confirms it stored the file intact;
                // a disconnect before that leaves it queued (Interrupted) instead.
                string ack;
                try
                {
                    // The receiver hashes the whole file before acking: allow for large files.
                    ack = await outgoing.FinalAck.Task.WaitAsync(FinalAckTimeout(size), cts.Token);
                }
                catch (TimeoutException)
                {
                    // No answer but still connected: keep it queued; the next connection's
                    // re-offer gets "ok" straight away if the receiver did store it.
                    lock (outgoing) outgoing.Disconnected = true;
                    throw new OperationCanceledException();
                }
                status = ack == FileAckStatus.Ok ? TransferStatus.Completed : StatusFor(ack);
                if (ack != FileAckStatus.Ok) lock (outgoing) outgoing.PeerVerdict = ack;
            }
            else
            {
                status = TransferStatus.Completed;
            }
        }
        catch (OperationCanceledException)
        {
            status = outgoing.Disconnected ? TransferStatus.Interrupted : TransferStatus.Cancelled;
        }
        catch (TimeoutException)
        {
            // Peer advertised resume but never answered file.start.
            status = resumable && !channel.IsConnected ? TransferStatus.Interrupted : TransferStatus.Failed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidDataException)
        {
            // A lost connection is resumable; a local file problem is not.
            status = resumable && !localError && !channel.IsConnected ? TransferStatus.Interrupted : TransferStatus.Failed;
        }

        bool peerAborted;
        lock (outgoing)
        {
            outgoing.Finished = true;
            peerAborted = outgoing.PeerVerdict is not null;
            progress.Status = peerAborted ? StatusFor(outgoing.PeerVerdict!) : status;
        }

        switch (progress.Status)
        {
            case TransferStatus.Completed when resumable:
                _outgoing.TryRemove(transferId, out _); // acknowledged
                State.RemoveOutbox(transferId);
                break;
            case TransferStatus.Completed:
                break; // legacy peer: stays in _outgoing until file.ack
            case TransferStatus.Interrupted:
                _outgoing.TryRemove(transferId, out _); // outbox entry kept: offered again on reconnect
                break;
            default:
                // Tell the receiver to drop its partial file, unless it already gave up on its own.
                if (started && !peerAborted) await TrySendCancelAsync(channel, transferId);
                _outgoing.TryRemove(transferId, out _);
                State.RemoveOutbox(transferId);
                break;
        }

        ProgressChanged?.Invoke(progress);
        return progress;
    }

    private static IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System, // no symlink/junction loops
            MaxRecursionDepth = SafeRelativePath.MaxDepth - 1,
        };
        return root.EnumerateFiles("*", options).OrderBy(f => f.FullName, StringComparer.Ordinal);
    }

    /// <summary>60 s plus the time to hash the file at a slow 50 MB/s.</summary>
    public static TimeSpan FinalAckTimeout(long size) => TimeSpan.FromSeconds(60 + size / (50.0 * 1024 * 1024));

    private static TransferStatus StatusFor(string ackStatus) =>
        ackStatus == FileAckStatus.Cancelled ? TransferStatus.Cancelled : TransferStatus.Failed;

    // ---------------------------------------------------------------- receiving

    /// <summary>Feed every incoming file.* message here; completed files land in <paramref name="downloadDir"/>.</summary>
    public async Task<TransferProgress?> HandleIncomingAsync(EncryptedChannel channel, BridgeMessage message, string downloadDir, CancellationToken ct = default)
    {
        switch (message.Type)
        {
            case MessageType.FileStart:
                return await HandleStartAsync(channel, message.PayloadAs<FileStartPayload>(), downloadDir, ct);

            case MessageType.FileChunk:
            {
                var chunk = message.PayloadAs<FileChunkPayload>();
                if (!TryGetIncoming(chunk.TransferId, out var state)) return null;

                byte[] bytes;
                try { bytes = Convert.FromBase64String(chunk.Data); }
                catch (FormatException) { return await FailIncomingAsync(channel, state, FileAckStatus.HashMismatch, ct); }

                if (chunk.Index != state.NextIndex || state.BytesWritten + bytes.Length > state.Meta.Size)
                    return await FailIncomingAsync(channel, state, FileAckStatus.HashMismatch, ct);

                try
                {
                    await state.Stream.WriteAsync(bytes, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Disk full / drive removed: tell the sender now so it stops.
                    return await FailIncomingAsync(channel, state, FileAckStatus.Cancelled, ct);
                }
                state.NextIndex++;
                state.BytesWritten += bytes.Length;
                return Report(Progress(state, TransferStatus.InProgress));
            }

            case MessageType.FileEnd:
                return await HandleEndAsync(channel, message.PayloadAs<FileEndPayload>(), downloadDir, ct);

            case MessageType.FileCancel:
            {
                var cancel = message.PayloadAs<FileCancelPayload>();
                // The peer may be cancelling something we are sending to it…
                if (_outgoing.TryGetValue(cancel.TransferId, out var outgoing))
                {
                    lock (outgoing)
                    {
                        if (!outgoing.Finished)
                        {
                            outgoing.PeerVerdict = FileAckStatus.Cancelled;
                            outgoing.Cts.Cancel();
                        }
                    }
                }
                State.RemoveOutbox(cancel.TransferId);
                // …or something it was sending to us.
                if (!TryGetIncoming(cancel.TransferId, out var state)) return null;
                RemoveIncoming(state);
                await DisposeQuietlyAsync(state.Stream);
                DeletePartial(state);
                return Report(Progress(state, TransferStatus.Cancelled));
            }

            case MessageType.FileResume:
            {
                var resume = message.PayloadAs<FileResumePayload>();
                if (_outgoing.TryGetValue(resume.TransferId, out var outgoing)) outgoing.ResumeReply.TrySetResult(resume.NextIndex);
                return null;
            }

            case MessageType.FileAck:
            {
                var ack = message.PayloadAs<FileAckPayload>();
                if (!_outgoing.TryGetValue(ack.TransferId, out var outgoing)) { if (ack.Status == FileAckStatus.Ok) State.RemoveOutbox(ack.TransferId); return null; }
                lock (outgoing)
                {
                    if (outgoing.AwaitingFinalAck && !outgoing.Finished && outgoing.ResumeReply.Task.IsCompleted)
                    {
                        outgoing.FinalAck.TrySetResult(ack.Status); // resumable sender waiting after file.end
                        return null;
                    }
                }
                if (ack.Status == FileAckStatus.Ok)
                {
                    lock (outgoing)
                    {
                        if (!outgoing.Finished)
                        {
                            // Answer to a resumable file.start: the receiver already has it.
                            outgoing.ResumeReply.TrySetResult(-1);
                            return null;
                        }
                    }
                    _outgoing.TryRemove(ack.TransferId, out _);
                    State.RemoveOutbox(ack.TransferId);
                    return outgoing.Progress;
                }
                lock (outgoing)
                {
                    outgoing.PeerVerdict = ack.Status;
                    if (!outgoing.Finished)
                    {
                        outgoing.Cts.Cancel(); // still sending: stop; the sender reports the verdict
                        return null;
                    }
                    outgoing.Progress.Status = StatusFor(ack.Status);
                }
                _outgoing.TryRemove(ack.TransferId, out _);
                State.RemoveOutbox(ack.TransferId);
                return Report(outgoing.Progress);
            }

            default:
                return null;
        }
    }

    private async Task<TransferProgress?> HandleStartAsync(EncryptedChannel channel, FileStartPayload meta, string downloadDir, CancellationToken ct)
    {
        var peerId = channel.PeerDeviceId;
        bool busy;
        lock (_incomingGate) busy = meta.TransferId is not null && _incoming.ContainsKey(meta.TransferId) || _incoming.Count >= MaxConcurrentIncoming;
        if (string.IsNullOrEmpty(meta.TransferId) || meta.Size < 0 || meta.TotalChunks < 0 ||
            meta.TotalChunks != (int)((meta.Size + ChunkSize - 1) / ChunkSize) || busy ||
            (meta.RelativePath is not null && SafeRelativePath.Segments(meta.RelativePath) is not { Count: > 0 }))
        {
            await SendAckAsync(channel, meta.TransferId ?? string.Empty, FileAckStatus.Cancelled, ct);
            return null;
        }

        var safeName = SafeFileName.Sanitize(meta.FileName);
        var resumable = meta.Resumable == true;

        if (resumable && State.CompletedPath(peerId, meta.TransferId, meta.Sha256) is { } savedPath && File.Exists(savedPath))
        {
            // Re-offer of a file we already stored (the sender missed our ack): don't store it twice.
            await SendAckAsync(channel, meta.TransferId, FileAckStatus.Ok, ct);
            return Report(new TransferProgress
            {
                TransferId = meta.TransferId, FileName = safeName, Direction = TransferDirection.Receiving,
                Status = TransferStatus.Completed, TotalChunks = meta.TotalChunks, ChunksDone = meta.TotalChunks,
                SavedPath = savedPath, RelativePath = meta.RelativePath, BatchId = meta.BatchId,
                BatchIndex = meta.BatchIndex, BatchCount = meta.BatchCount,
            });
        }

        Incoming state;
        try
        {
            Directory.CreateDirectory(downloadDir);
            state = resumable ? OpenResumable(channel, peerId, meta, safeName, downloadDir) : OpenFresh(channel, peerId, meta, safeName, downloadDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't store it (disk full, permissions): tell the sender now rather than let it stream for nothing.
            await SendAckAsync(channel, meta.TransferId, FileAckStatus.Cancelled, ct);
            return Report(new TransferProgress
            {
                TransferId = meta.TransferId, FileName = safeName, Direction = TransferDirection.Receiving, Status = TransferStatus.Failed,
            });
        }
        lock (_incomingGate) _incoming[meta.TransferId] = state;

        if (resumable)
            await channel.SendAsync(BridgeMessage.Create(MessageType.FileResume, new FileResumePayload(meta.TransferId, state.NextIndex)), ct);
        return Report(Progress(state, TransferStatus.InProgress));
    }

    private static Incoming OpenFresh(EncryptedChannel channel, string peerId, FileStartPayload meta, string safeName, string downloadDir)
    {
        var tempPath = Path.Combine(downloadDir, $".bridge-{Guid.NewGuid():N}.part");
        return new Incoming
        {
            Channel = channel, PeerId = peerId, Meta = meta, SafeName = safeName, TempPath = tempPath,
            Stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, useAsync: true),
        };
    }

    /// <summary>Reopens a matching partial file from an earlier connection, or starts a new resumable one.</summary>
    private static Incoming OpenResumable(EncryptedChannel channel, string peerId, FileStartPayload meta, string safeName, string downloadDir)
    {
        var partialDir = Path.Combine(downloadDir, PartialDirName);
        Directory.CreateDirectory(partialDir);
        // "<peer prefix>-<peer+transfer hash>": the prefix lets us cap partials per peer.
        var peerPrefix = Hex(peerId)[..8];
        var key = peerPrefix + "-" + Hex(TransferStateStore.Key(peerId, meta.TransferId))[..24];
        var partPath = Path.Combine(partialDir, key + ".part");
        var metaPath = Path.Combine(partialDir, key + ".json");
        var metaJson = JsonSerializer.Serialize(meta, BridgeJson.Options);

        var reuse = File.Exists(partPath) && File.Exists(metaPath) && SafeReadAllText(metaPath) == metaJson;
        if (!reuse)
        {
            TryDelete(partPath);
            EvictOldPartials(partialDir, peerPrefix, keep: MaxPartialsPerPeer - 1);
            File.WriteAllText(metaPath, metaJson);
        }

        var stream = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, ChunkSize, useAsync: true);
        var length = stream.Length;
        int nextIndex;
        long kept;
        if (length > meta.Size) { nextIndex = 0; kept = 0; }                    // corrupt: start over
        else if (length == meta.Size) { nextIndex = meta.TotalChunks; kept = length; }
        else { nextIndex = (int)(length / ChunkSize); kept = (long)nextIndex * ChunkSize; } // drop a half-written chunk
        stream.SetLength(kept);
        stream.Seek(kept, SeekOrigin.Begin);
        File.SetLastWriteTimeUtc(metaPath, DateTime.UtcNow); // keeps the partial alive for another 24 h

        return new Incoming
        {
            Channel = channel, PeerId = peerId, Meta = meta, SafeName = safeName, TempPath = partPath, MetaPath = metaPath,
            Stream = stream, NextIndex = nextIndex, BytesWritten = kept, ResumedFrom = nextIndex,
        };
    }

    private static string Hex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Keeps at most <paramref name="keep"/> of one peer's abandoned partials (oldest go first).</summary>
    private static void EvictOldPartials(string partialDir, string peerPrefix, int keep)
    {
        var metas = new DirectoryInfo(partialDir).GetFiles(peerPrefix + "-*.json").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        foreach (var old in metas.Skip(keep))
        {
            TryDelete(old.FullName);
            TryDelete(Path.ChangeExtension(old.FullName, ".part"));
        }
    }

    private async Task<TransferProgress?> HandleEndAsync(EncryptedChannel channel, FileEndPayload end, string downloadDir, CancellationToken ct)
    {
        if (!TryGetIncoming(end.TransferId, out var state)) return null;
        RemoveIncoming(state);
        string finalPath;
        try
        {
            await state.Stream.DisposeAsync(); // flushes: can itself fail when the disk is full
            var actualHash = await FileHasher.Sha256HexAsync(state.TempPath, ct);
            if (state.BytesWritten != state.Meta.Size ||
                !string.Equals(actualHash, state.Meta.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                DeletePartial(state);
                await SendAckAsync(channel, end.TransferId, FileAckStatus.HashMismatch, ct);
                return Report(Progress(state, TransferStatus.Failed));
            }

            var targetDir = downloadDir;
            if (state.Meta.RelativePath is { } rel)
            {
                var top = SafeRelativePath.Segments(rel)![0];
                var batchKey = state.Meta.BatchId ?? state.Meta.TransferId;
                var rootName = State.BatchRootName(state.PeerId, batchKey, () => SafeRelativePath.UniqueRootName(downloadDir, top));
                targetDir = SafeRelativePath.Resolve(downloadDir, rootName, rel) ?? throw new IOException("Unsafe folder path");
                Directory.CreateDirectory(targetDir);
            }
            finalPath = SafeFileName.UniquePath(targetDir, state.SafeName);
            File.Move(state.TempPath, finalPath, overwrite: false);
            if (state.MetaPath is not null) TryDelete(state.MetaPath);
            if (state.Resumable) State.MarkCompleted(state.PeerId, state.Meta.TransferId, finalPath, state.Meta.Sha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Couldn't finish storing it: the sender must not believe it arrived.
            DeletePartial(state);
            await SendAckAsync(channel, end.TransferId, FileAckStatus.Cancelled, ct);
            return Report(Progress(state, TransferStatus.Failed));
        }
        await SendAckAsync(channel, end.TransferId, FileAckStatus.Ok, ct);

        var done = Progress(state, TransferStatus.Completed);
        done.SavedPath = finalPath;
        return Report(done);
    }

    /// <summary>
    /// Call when a session ends. Resumable partial files are kept (and reported
    /// Interrupted); other partials are deleted. Outgoing sends on this channel stop:
    /// resumable ones become Interrupted and stay queued.
    /// </summary>
    public void AbortAll(EncryptedChannel channel)
    {
        List<Incoming> mine;
        lock (_incomingGate)
        {
            mine = _incoming.Values.Where(s => ReferenceEquals(s.Channel, channel)).ToList();
            foreach (var s in mine) _incoming.Remove(s.Meta.TransferId);
        }
        foreach (var state in mine)
        {
            try { state.Stream.Dispose(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (state.Resumable)
            {
                Report(Progress(state, TransferStatus.Interrupted));
            }
            else
            {
                TryDelete(state.TempPath);
                Report(Progress(state, TransferStatus.Failed));
            }
        }

        foreach (var (id, outgoing) in _outgoing)
        {
            if (!ReferenceEquals(outgoing.Channel, channel)) continue;
            lock (outgoing)
            {
                if (!outgoing.Finished)
                {
                    outgoing.Disconnected = true;
                    outgoing.Cts.Cancel();
                }
            }
            // A sent file whose ack was lost keeps "Completed"; a resumable one is re-offered from the outbox.
            if (outgoing.Finished) _outgoing.TryRemove(id, out _);
        }
    }

    /// <summary>Deletes resumable partial files nobody came back for within 24 h.</summary>
    public static void CleanupExpiredPartials(string downloadDir, TimeProvider? time = null)
    {
        var cutoff = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime - TransferStateStore.Lifetime;
        try
        {
            // Non-resumable temp files left behind if the app was killed mid-transfer (only ours: ".bridge-*.part").
            if (Directory.Exists(downloadDir))
                foreach (var f in Directory.GetFiles(downloadDir, ".bridge-*.part"))
                    if (File.GetLastWriteTimeUtc(f) < cutoff) TryDelete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var dir = Path.Combine(downloadDir, PartialDirName);
        if (!Directory.Exists(dir)) return;
        try
        {
            // Decide per partial (stem) first, then delete: a pair's age is its .json's
            // timestamp (refreshed on every resume), or the .part's when there's no .json.
            foreach (var group in Directory.GetFiles(dir).GroupBy(f => Path.GetFileNameWithoutExtension(f)).ToList())
            {
                var metaPath = Path.Combine(dir, group.Key + ".json");
                var stamp = File.Exists(metaPath) ? File.GetLastWriteTimeUtc(metaPath) : group.Max(File.GetLastWriteTimeUtc);
                if (stamp < cutoff) foreach (var f in group) TryDelete(f);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ helpers

    private bool TryGetIncoming(string transferId, out Incoming state)
    {
        lock (_incomingGate) return _incoming.TryGetValue(transferId, out state!);
    }

    private void RemoveIncoming(Incoming state)
    {
        lock (_incomingGate) _incoming.Remove(state.Meta.TransferId);
    }

    private async Task<TransferProgress> FailIncomingAsync(EncryptedChannel channel, Incoming state, string status, CancellationToken ct)
    {
        RemoveIncoming(state);
        await DisposeQuietlyAsync(state.Stream);
        DeletePartial(state);
        await SendAckAsync(channel, state.Meta.TransferId, status, ct);
        return Report(Progress(state, TransferStatus.Failed));
    }

    private static void DeletePartial(Incoming state)
    {
        TryDelete(state.TempPath);
        if (state.MetaPath is not null) TryDelete(state.MetaPath);
    }

    private static Task SendAckAsync(EncryptedChannel channel, string transferId, string status, CancellationToken ct) =>
        channel.SendAsync(BridgeMessage.Create(MessageType.FileAck, new FileAckPayload(transferId, status)), ct);

    private static TransferProgress Progress(Incoming state, TransferStatus status) => new()
    {
        TransferId = state.Meta.TransferId, FileName = state.SafeName,
        Direction = TransferDirection.Receiving, Status = status,
        TotalChunks = state.Meta.TotalChunks, ChunksDone = state.NextIndex, ResumedFrom = state.ResumedFrom,
        RelativePath = state.Meta.RelativePath, BatchId = state.Meta.BatchId,
        BatchIndex = state.Meta.BatchIndex, BatchCount = state.Meta.BatchCount,
    };

    private TransferProgress Report(TransferProgress progress)
    {
        ProgressChanged?.Invoke(progress);
        return progress;
    }

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static async Task TrySendCancelAsync(EncryptedChannel channel, string transferId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await channel.SendAsync(BridgeMessage.Create(MessageType.FileCancel, new FileCancelPayload(transferId)), timeout.Token);
        }
        catch { /* channel already gone */ }
    }

    private static async Task DisposeQuietlyAsync(Stream stream)
    {
        try { await stream.DisposeAsync(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string? SafeReadAllText(string path)
    {
        try { return File.ReadAllText(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
