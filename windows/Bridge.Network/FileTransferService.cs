using System.Collections.Concurrent;
using Bridge.Protocol;
using Bridge.Security;

namespace Bridge.Network;

public enum TransferDirection { Sending, Receiving }
public enum TransferStatus { InProgress, Completed, Failed, Cancelled }

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
    public double Fraction => TotalChunks == 0 ? (Status == TransferStatus.Completed ? 1 : 0) : (double)ChunksDone / TotalChunks;
}

/// <summary>
/// Chunked, integrity-checked file transfer (protocol/protocol.md). Chunk size
/// chosen to stay comfortably under typical socket buffers once
/// base64-encoded (64 KB raw -> ~87 KB encoded).
///
/// Incoming file names are sanitised (no path traversal, no overwriting existing
/// files); chunks must arrive in order and never exceed the announced size.
/// </summary>
public sealed class FileTransferService
{
    public const int ChunkSize = 64 * 1024;
    public const int MaxConcurrentIncoming = 8;

    private sealed class Incoming
    {
        public required FileStartPayload Meta { get; init; }
        public required string SafeName { get; init; }
        public required string TempPath { get; init; }
        public required FileStream Stream { get; init; }
        public int NextIndex { get; set; }
        public long BytesWritten { get; set; }
    }

    /// <summary>
    /// An outgoing transfer, kept from file.start until the receiver's file.ack.
    /// The receiver may ack early (rejecting file.start, or failing mid-transfer);
    /// that verdict stops the sender instead of being ignored.
    /// </summary>
    private sealed class Outgoing
    {
        public required TransferProgress Progress { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public string? PeerVerdict { get; set; } // non-ok file.ack status, if any
        public bool Finished { get; set; }       // sender loop has returned
    }

    private readonly ConcurrentDictionary<string, Outgoing> _outgoing = new();
    private readonly Dictionary<string, Incoming> _incoming = new(); // only touched by the session's receive loop

    public event Action<TransferProgress>? ProgressChanged;

    /// <summary>Sends a file; never throws for I/O or cancellation (the returned status says what happened).</summary>
    public async Task<TransferProgress> SendFileAsync(EncryptedChannel channel, string filePath, CancellationToken ct = default)
    {
        var transferId = Guid.NewGuid().ToString();
        var fileInfo = new FileInfo(filePath);
        var progress = new TransferProgress
        {
            TransferId = transferId,
            FileName = fileInfo.Name,
            Direction = TransferDirection.Sending,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var outgoing = new Outgoing { Progress = progress, Cts = cts };
        _outgoing[transferId] = outgoing;
        var started = false;
        TransferStatus status;
        try
        {
            var hash = await FileHasher.Sha256HexAsync(filePath, cts.Token);
            var size = fileInfo.Length;
            progress.TotalChunks = (int)((size + ChunkSize - 1) / ChunkSize);
            ProgressChanged?.Invoke(progress);

            await channel.SendAsync(BridgeMessage.Create(MessageType.FileStart,
                new FileStartPayload(transferId, fileInfo.Name, size, hash, progress.TotalChunks)), cts.Token);
            started = true;

            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true);
            var buffer = new byte[ChunkSize];
            for (var index = 0; index < progress.TotalChunks; index++)
            {
                var read = await ReadFullAsync(stream, buffer, cts.Token);
                if (read == 0) throw new IOException("File shrank while it was being sent");
                await channel.SendAsync(BridgeMessage.Create(MessageType.FileChunk,
                    new FileChunkPayload(transferId, index, Convert.ToBase64String(buffer, 0, read))), cts.Token);

                progress.ChunksDone = index + 1;
                ProgressChanged?.Invoke(progress);
            }

            await channel.SendAsync(BridgeMessage.Create(MessageType.FileEnd, new FileEndPayload(transferId)), cts.Token);
            status = TransferStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            status = TransferStatus.Cancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            status = TransferStatus.Failed;
        }

        bool peerAborted;
        lock (outgoing)
        {
            outgoing.Finished = true;
            peerAborted = outgoing.PeerVerdict is not null;
            progress.Status = peerAborted ? StatusFor(outgoing.PeerVerdict!) : status;
        }
        // Tell the receiver to drop its partial file, unless it already gave up on its own.
        if (started && !peerAborted && progress.Status != TransferStatus.Completed) await TrySendCancelAsync(channel, transferId);
        if (progress.Status != TransferStatus.Completed) _outgoing.TryRemove(transferId, out _); // else: await file.ack

        ProgressChanged?.Invoke(progress);
        return progress;
    }

    public void CancelTransfer(string transferId)
    {
        if (!_outgoing.TryGetValue(transferId, out var outgoing)) return;
        lock (outgoing)
        {
            if (!outgoing.Finished) outgoing.Cts.Cancel();
        }
    }

    private static TransferStatus StatusFor(string ackStatus) =>
        ackStatus == FileAckStatus.Cancelled ? TransferStatus.Cancelled : TransferStatus.Failed;

    /// <summary>Feed every incoming file.* message here; completed files land in <paramref name="downloadDir"/>.</summary>
    public async Task<TransferProgress?> HandleIncomingAsync(EncryptedChannel channel, BridgeMessage message, string downloadDir, CancellationToken ct = default)
    {
        switch (message.Type)
        {
            case MessageType.FileStart:
            {
                var meta = message.PayloadAs<FileStartPayload>();
                if (string.IsNullOrEmpty(meta.TransferId) || meta.Size < 0 || meta.TotalChunks < 0 ||
                    _incoming.ContainsKey(meta.TransferId) || _incoming.Count >= MaxConcurrentIncoming)
                {
                    await channel.SendAsync(BridgeMessage.Create(MessageType.FileAck,
                        new FileAckPayload(meta.TransferId ?? string.Empty, FileAckStatus.Cancelled)), ct);
                    return null;
                }

                var safeName = SafeFileName.Sanitize(meta.FileName);
                var tempPath = Path.Combine(downloadDir, $".bridge-{Guid.NewGuid():N}.part");
                FileStream tempStream;
                try
                {
                    Directory.CreateDirectory(downloadDir);
                    tempStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, useAsync: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Can't store it (disk full, permissions): tell the sender now rather than let it stream for nothing.
                    await channel.SendAsync(BridgeMessage.Create(MessageType.FileAck,
                        new FileAckPayload(meta.TransferId, FileAckStatus.Cancelled)), ct);
                    return Report(new TransferProgress
                    {
                        TransferId = meta.TransferId, FileName = safeName,
                        Direction = TransferDirection.Receiving, Status = TransferStatus.Failed,
                    });
                }
                _incoming[meta.TransferId] = new Incoming
                {
                    Meta = meta,
                    SafeName = safeName,
                    TempPath = tempPath,
                    Stream = tempStream,
                };
                return Report(new TransferProgress
                {
                    TransferId = meta.TransferId, FileName = safeName,
                    Direction = TransferDirection.Receiving, TotalChunks = meta.TotalChunks,
                });
            }
            case MessageType.FileChunk:
            {
                var chunk = message.PayloadAs<FileChunkPayload>();
                if (!_incoming.TryGetValue(chunk.TransferId, out var state)) return null;

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
                return Report(new TransferProgress
                {
                    TransferId = chunk.TransferId, FileName = state.SafeName,
                    Direction = TransferDirection.Receiving, TotalChunks = state.Meta.TotalChunks,
                    ChunksDone = state.NextIndex,
                });
            }
            case MessageType.FileEnd:
            {
                var end = message.PayloadAs<FileEndPayload>();
                if (!_incoming.TryGetValue(end.TransferId, out var state)) return null;
                _incoming.Remove(end.TransferId);
                string actualHash;
                string finalPath;
                try
                {
                    await state.Stream.DisposeAsync(); // flushes: can itself fail when the disk is full
                    actualHash = await FileHasher.Sha256HexAsync(state.TempPath, ct);
                    if (state.BytesWritten != state.Meta.Size ||
                        !string.Equals(actualHash, state.Meta.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(state.TempPath);
                        await channel.SendAsync(BridgeMessage.Create(MessageType.FileAck,
                            new FileAckPayload(end.TransferId, FileAckStatus.HashMismatch)), ct);
                        return Report(Finished(state, TransferStatus.Failed));
                    }
                    finalPath = SafeFileName.UniquePath(downloadDir, state.SafeName);
                    File.Move(state.TempPath, finalPath, overwrite: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Couldn't finish storing it: the sender must not believe it arrived.
                    TryDelete(state.TempPath);
                    await channel.SendAsync(BridgeMessage.Create(MessageType.FileAck,
                        new FileAckPayload(end.TransferId, FileAckStatus.Cancelled)), ct);
                    return Report(Finished(state, TransferStatus.Failed));
                }
                await channel.SendAsync(BridgeMessage.Create(MessageType.FileAck,
                    new FileAckPayload(end.TransferId, FileAckStatus.Ok)), ct);

                var done = Finished(state, TransferStatus.Completed);
                done.SavedPath = finalPath;
                return Report(done);
            }
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
                // …or something it was sending to us.
                if (!_incoming.TryGetValue(cancel.TransferId, out var state)) return null;
                _incoming.Remove(cancel.TransferId);
                await state.Stream.DisposeAsync();
                TryDelete(state.TempPath);
                return Report(Finished(state, TransferStatus.Cancelled));
            }
            case MessageType.FileAck:
            {
                var ack = message.PayloadAs<FileAckPayload>();
                if (!_outgoing.TryGetValue(ack.TransferId, out var outgoing)) return null;
                if (ack.Status == FileAckStatus.Ok)
                {
                    _outgoing.TryRemove(ack.TransferId, out _);
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
                return Report(outgoing.Progress);
            }
            default:
                return null;
        }
    }

    /// <summary>Call when the session ends: drops partially received files and stops outgoing sends.</summary>
    public void AbortAll()
    {
        foreach (var state in _incoming.Values)
        {
            try { state.Stream.Dispose(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            TryDelete(state.TempPath);
            Report(Finished(state, TransferStatus.Failed));
        }
        _incoming.Clear();

        foreach (var (id, outgoing) in _outgoing)
        {
            lock (outgoing)
            {
                if (!outgoing.Finished) outgoing.Cts.Cancel();
            }
            _outgoing.TryRemove(id, out _); // a sent file whose ack was lost keeps its "Completed" status
        }
    }

    private async Task<TransferProgress> FailIncomingAsync(EncryptedChannel channel, Incoming state, string status, CancellationToken ct)
    {
        _incoming.Remove(state.Meta.TransferId);
        try { await state.Stream.DisposeAsync(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        TryDelete(state.TempPath);
        await channel.SendAsync(BridgeMessage.Create(MessageType.FileAck, new FileAckPayload(state.Meta.TransferId, status)), ct);
        return Report(Finished(state, TransferStatus.Failed));
    }

    private static TransferProgress Finished(Incoming state, TransferStatus status) => new()
    {
        TransferId = state.Meta.TransferId, FileName = state.SafeName,
        Direction = TransferDirection.Receiving, Status = status,
        TotalChunks = state.Meta.TotalChunks, ChunksDone = state.NextIndex,
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
