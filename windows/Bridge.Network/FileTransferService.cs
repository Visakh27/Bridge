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
    public double Fraction => TotalChunks == 0 ? 0 : (double)ChunksDone / TotalChunks;
}

/// <summary>
/// Chunked, resumable, integrity-checked file transfer per section 9 of the
/// blueprint. Chunk size chosen to stay comfortably under typical socket
/// buffers once base64-encoded (64 KB raw -> ~87 KB encoded).
/// </summary>
public sealed class FileTransferService
{
    public const int ChunkSize = 64 * 1024;

    private readonly Dictionary<string, CancellationTokenSource> _activeCancellations = new();
    private readonly Dictionary<string, FileStream> _incomingStreams = new();
    private readonly Dictionary<string, string> _incomingTempPaths = new();
    private readonly Dictionary<string, FileStartPayload> _incomingMeta = new();

    public event Action<TransferProgress>? ProgressChanged;

    public async Task SendFileAsync(EncryptedChannel channel, string filePath, CancellationToken ct = default)
    {
        var transferId = Guid.NewGuid().ToString();
        var fileInfo = new FileInfo(filePath);
        var hash = await FileHasher.Sha256HexAsync(filePath, ct);
        var totalChunks = (int)Math.Ceiling(fileInfo.Length / (double)ChunkSize);

        var progress = new TransferProgress
        {
            TransferId = transferId,
            FileName = fileInfo.Name,
            Direction = TransferDirection.Sending,
            TotalChunks = totalChunks,
        };
        ProgressChanged?.Invoke(progress);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeCancellations[transferId] = cts;

        await channel.SendAsync(BridgeMessage.Create(MessageType.FileStart,
            new FileStartPayload(transferId, fileInfo.Name, fileInfo.Length, hash, totalChunks)), cts.Token);

        await using var stream = File.OpenRead(filePath);
        var buffer = new byte[ChunkSize];
        for (var index = 0; index < totalChunks; index++)
        {
            if (cts.IsCancellationRequested)
            {
                progress.Status = TransferStatus.Cancelled;
                ProgressChanged?.Invoke(progress);
                return;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(0, ChunkSize), cts.Token);
            var chunkData = Convert.ToBase64String(buffer, 0, read);
            await channel.SendAsync(BridgeMessage.Create(MessageType.FileChunk,
                new FileChunkPayload(transferId, index, chunkData)), cts.Token);

            progress.ChunksDone = index + 1;
            ProgressChanged?.Invoke(progress);
        }

        await channel.SendAsync(BridgeMessage.Create(MessageType.FileEnd, new FileEndPayload(transferId)), cts.Token);
        progress.Status = TransferStatus.Completed;
        ProgressChanged?.Invoke(progress);
        _activeCancellations.Remove(transferId);
    }

    public void CancelTransfer(string transferId)
    {
        if (_activeCancellations.TryGetValue(transferId, out var cts)) cts.Cancel();
    }

    /// <summary>Feed every incoming BridgeMessage here; handles file.start/chunk/end and writes to downloadDir.</summary>
    public async Task<TransferProgress?> HandleIncomingAsync(EncryptedChannel channel, BridgeMessage message, string downloadDir, CancellationToken ct = default)
    {
        switch (message.Type)
        {
            case MessageType.FileStart:
            {
                var meta = message.PayloadAs<FileStartPayload>();
                _incomingMeta[meta.TransferId] = meta;
                Directory.CreateDirectory(downloadDir);
                var tempPath = Path.Combine(downloadDir, meta.FileName + ".part");
                _incomingTempPaths[meta.TransferId] = tempPath;
                _incomingStreams[meta.TransferId] = File.Create(tempPath);
                var progress = new TransferProgress
                {
                    TransferId = meta.TransferId,
                    FileName = meta.FileName,
                    Direction = TransferDirection.Receiving,
                    TotalChunks = meta.TotalChunks,
                };
                ProgressChanged?.Invoke(progress);
                return progress;
            }
            case MessageType.FileChunk:
            {
                var chunk = message.PayloadAs<FileChunkPayload>();
                if (!_incomingStreams.TryGetValue(chunk.TransferId, out var fs)) return null;
                var bytes = Convert.FromBase64String(chunk.Data);
                await fs.WriteAsync(bytes, ct);
                var meta = _incomingMeta[chunk.TransferId];
                var progress = new TransferProgress
                {
                    TransferId = chunk.TransferId,
                    FileName = meta.FileName,
                    Direction = TransferDirection.Receiving,
                    TotalChunks = meta.TotalChunks,
                    ChunksDone = chunk.Index + 1,
                };
                ProgressChanged?.Invoke(progress);
                return progress;
            }
            case MessageType.FileEnd:
            {
                var end = message.PayloadAs<FileEndPayload>();
                if (!_incomingStreams.TryGetValue(end.TransferId, out var fs)) return null;
                await fs.DisposeAsync();
                _incomingStreams.Remove(end.TransferId);

                var meta = _incomingMeta[end.TransferId];
                var tempPath = _incomingTempPaths[end.TransferId];
                var actualHash = await FileHasher.Sha256HexAsync(tempPath, ct);

                if (actualHash != meta.Sha256)
                {
                    await channel.SendAsync(BridgeMessage.Create(MessageType.FileAck,
                        new FileAckPayload(end.TransferId, "hash_mismatch")), ct);
                    File.Delete(tempPath);
                    return new TransferProgress
                    {
                        TransferId = end.TransferId, FileName = meta.FileName,
                        Direction = TransferDirection.Receiving, Status = TransferStatus.Failed,
                        TotalChunks = meta.TotalChunks, ChunksDone = meta.TotalChunks,
                    };
                }

                var finalPath = Path.Combine(Path.GetDirectoryName(tempPath)!, meta.FileName);
                File.Move(tempPath, finalPath, overwrite: true);
                await channel.SendAsync(BridgeMessage.Create(MessageType.FileAck,
                    new FileAckPayload(end.TransferId, "ok")), ct);

                var progress = new TransferProgress
                {
                    TransferId = end.TransferId, FileName = meta.FileName,
                    Direction = TransferDirection.Receiving, Status = TransferStatus.Completed,
                    TotalChunks = meta.TotalChunks, ChunksDone = meta.TotalChunks,
                };
                ProgressChanged?.Invoke(progress);
                return progress;
            }
            case MessageType.FileCancel:
            {
                var cancel = message.PayloadAs<FileCancelPayload>();
                if (_incomingStreams.TryGetValue(cancel.TransferId, out var fs))
                {
                    await fs.DisposeAsync();
                    _incomingStreams.Remove(cancel.TransferId);
                    if (_incomingTempPaths.TryGetValue(cancel.TransferId, out var p) && File.Exists(p)) File.Delete(p);
                }
                return null;
            }
            default:
                return null;
        }
    }
}
