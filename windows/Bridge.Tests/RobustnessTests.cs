using System.Buffers.Binary;
using Bridge.Core;
using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

/// <summary>Review pass 3: failure paths and hostile input on the PC side.</summary>
public sealed class RobustnessTests : IDisposable
{
    private readonly string _downloads = Loopback.TempDir();
    private readonly DeviceIdentity _pc = DeviceIdentity.Generate();
    private readonly InMemoryPairedDeviceStore _store = new();
    private readonly TcpBridgeListener _listener = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly FileTransferService _files = new();
    private readonly ThrowingClipboard _clipboard = new();
    private readonly PairingService _pairing;
    private readonly BridgeHost _host;

    public RobustnessTests()
    {
        _pairing = new PairingService(_pc, _store, "PC");
        _host = new BridgeHost(_pc, _store, _pairing, _files, new ClipboardSyncService(_clipboard) { Enabled = true }, _downloads);
        _ = _host.RunAsync(_listener, _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _host.DisposeAsync().AsTask().Wait();
        _listener.Dispose();
        try { Directory.Delete(_downloads, true); } catch { }
    }

    private async Task<(EncryptedChannel Ch, BridgeSession Session)> PairAndConnectAsync()
    {
        var phone = DeviceIdentity.Generate();
        var qr = _pairing.CreateQrPayload(_listener.Port, "127.0.0.1");
        await using (var p = await EncryptedChannel.HandshakeAsInitiatorAsync(await TcpTransport.ConnectAsync("127.0.0.1", _listener.Port), phone, _pc.PublicKeyBlob))
        {
            await p.SendAsync(BridgeMessage.Create(MessageType.PairingRequest, new PairingRequestPayload(qr.PairingToken, "P", Convert.ToBase64String(phone.PublicKeyBlob))));
            Assert.Equal(MessageType.PairingAccept, (await p.ReceiveAsync().WithTimeout()).Type);
        }
        var started = new TaskCompletionSource<BridgeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.SessionStarted += s => started.TrySetResult(s);
        var ch = await EncryptedChannel.HandshakeAsInitiatorAsync(await TcpTransport.ConnectAsync("127.0.0.1", _listener.Port), phone, _pc.PublicKeyBlob);
        await ch.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload([Features.Files, Features.Clipboard])));
        Assert.Equal(MessageType.CapabilitiesAdvertise, (await ch.ReceiveAsync().WithTimeout()).Type);
        return (ch, await started.Task.WithTimeout());
    }

    /// <summary>Reads until a pong arrives; throws if the stream is broken.</summary>
    private static async Task PingAsync(EncryptedChannel ch)
    {
        await ch.SendAsync(BridgeMessage.CreateEmpty(MessageType.PresencePing));
        while ((await ch.ReceiveAsync().WithTimeout()).Type != MessageType.PresencePong) { }
    }

    [Fact]
    public async Task Cancelling_A_PC_Send_Keeps_The_Connection_Usable()
    {
        // Cancelling one transfer must not tear a frame or skip a nonce, which
        // would make the phone drop the whole connection.
        var (ch, session) = await PairAndConnectAsync();
        await using (ch)
        {
            var src = Path.Combine(Loopback.TempDir(), "big.bin");
            await File.WriteAllBytesAsync(src, new byte[FileTransferService.ChunkSize * 400]);
            for (var round = 0; round < 40; round++)
            {
                // Cancel from another thread at an arbitrary moment, as the UI would,
                // so it can land while a frame is half-written.
                var idSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnProgress(TransferProgress p)
                {
                    if (p.Direction == TransferDirection.Sending && p.ChunksDone >= 2) idSeen.TrySetResult(p.TransferId);
                }
                _files.ProgressChanged += OnProgress;
                var send = session.SendFileAsync(src);
                var delayTicks = Random.Shared.Next(0, 20_000);
                _ = Task.Run(async () =>
                {
                    var id = await idSeen.Task;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedTicks < delayTicks) { } // spin: sub-millisecond jitter
                    _files.CancelTransfer(id);
                });
                // Drain concurrently so the sender is never blocked on socket buffers.
                var drain = Task.Run(async () =>
                {
                    while (true)
                    {
                        var m = await ch.ReceiveAsync();
                        if (m.Type == MessageType.FileCancel || m.Type == MessageType.FileEnd) return m.Type;
                    }
                });
                var result = await send.WithTimeout(20);
                _files.ProgressChanged -= OnProgress;
                Assert.Equal(TransferStatus.Cancelled, result.Status);
                Assert.Equal(MessageType.FileCancel, await drain.WithTimeout());
                await PingAsync(ch); // the stream must still decrypt in order
            }
            Assert.True(session.IsRunning);
        }
    }

    [Fact]
    public async Task SendFile_Reports_Failure_Instead_Of_Throwing_When_Connection_Drops()
    {
        var (ch, session) = await PairAndConnectAsync();
        var src = Path.Combine(Loopback.TempDir(), "big.bin");
        await File.WriteAllBytesAsync(src, new byte[FileTransferService.ChunkSize * 2000]);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _files.ProgressChanged += p => { if (p.Direction == TransferDirection.Sending && p.ChunksDone == 3) first.TrySetResult(); };
        var send = session.SendFileAsync(src);
        _ = Task.Run(async () => { try { while (true) await ch.ReceiveAsync(); } catch { } });
        await first.Task.WithTimeout();
        await session.DisposeAsync(); // e.g. "Forget this phone" or app exit during a transfer

        var result = await send.WithTimeout(); // must not throw
        Assert.NotEqual(TransferStatus.Completed, result.Status);
        await ch.DisposeAsync();
    }

    [Fact]
    public async Task A_Failing_Clipboard_Does_Not_Drop_The_Session()
    {
        // Windows' clipboard throws (COMException CLIPBRD_E_CANT_OPEN) whenever
        // another app has it open; that must not disconnect the phone.
        var (ch, session) = await PairAndConnectAsync();
        await using (ch)
        {
            await ch.SendAsync(BridgeMessage.Create(MessageType.ClipboardUpdate, new ClipboardUpdatePayload("text", "x")));
            await PingAsync(ch);
            Assert.True(_clipboard.Attempts > 0);
            Assert.True(session.IsRunning);
        }
    }

    [Fact]
    public async Task Unauthenticated_Client_Cannot_Make_PC_Allocate_Large_Frames()
    {
        // Before the handshake nobody is authenticated; a HELLO is ~100 bytes.
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync("127.0.0.1", _listener.Port);
        var stream = client.GetStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, 16 * 1024 * 1024);
        await stream.WriteAsync(header);
        // The PC must hang up right away instead of waiting for (and buffering) 16 MB.
        var buf = new byte[1];
        var read = await stream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task Many_Idle_Unauthenticated_Connections_Do_Not_Block_Pairing()
    {
        var idle = new List<System.Net.Sockets.TcpClient>();
        try
        {
            for (var i = 0; i < 40; i++)
            {
                var c = new System.Net.Sockets.TcpClient();
                await c.ConnectAsync("127.0.0.1", _listener.Port);
                idle.Add(c);
            }
            var (ch, _) = await PairAndConnectAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await ch.DisposeAsync();
        }
        finally
        {
            foreach (var c in idle) c.Dispose();
        }
    }

    [Fact]
    public void SafeFileName_Strips_Bidi_Overrides_That_Spoof_Extensions()
    {
        // "photo‮gnp.exe" renders as "photoexe.png" in Explorer.
        var name = SafeFileName.Sanitize("photo‮gnp.exe");
        Assert.DoesNotContain("‮", name);
        Assert.Equal(".exe", Path.GetExtension(name));
        Assert.Equal("ab.txt", SafeFileName.Sanitize("a​b.txt")); // zero-width space
    }

    [Fact]
    public async Task Disk_Write_Failure_Is_Reported_To_Sender()
    {
        var (ch, _) = await PairAndConnectAsync();
        await using (ch)
        {
            await ch.SendAsync(BridgeMessage.Create(MessageType.FileStart, new FileStartPayload("w1", "a.bin", 200_000, "00", 4)));
            await Task.Delay(200);
            // Simulate the disk going away under the transfer.
            foreach (var part in Directory.GetFiles(_downloads, "*.part")) File.Delete(part);
            Directory.Delete(_downloads, true);
            File.WriteAllText(_downloads, "now a file, not a folder");
            try
            {
                for (var i = 0; i < 4; i++)
                    await ch.SendAsync(BridgeMessage.Create(MessageType.FileChunk, new FileChunkPayload("w1", i, Convert.ToBase64String(new byte[50_000]))));
                await ch.SendAsync(BridgeMessage.Create(MessageType.FileEnd, new FileEndPayload("w1")));
                BridgeMessage m;
                while ((m = await ch.ReceiveAsync().WithTimeout()).Type != MessageType.FileAck) { }
                Assert.NotEqual(FileAckStatus.Ok, m.PayloadAs<FileAckPayload>().Status);
            }
            finally
            {
                File.Delete(_downloads);
                Directory.CreateDirectory(_downloads);
            }
        }
    }

    private sealed class ThrowingClipboard : IClipboardAccess
    {
        public int Attempts;
        public event Action<string>? LocalClipboardChanged { add { } remove { } }
        public Task<string?> ReadTextAsync() => Task.FromResult<string?>(null);
        public Task WriteTextAsync(string text)
        {
            Interlocked.Increment(ref Attempts);
            throw new System.Runtime.InteropServices.COMException("OpenClipboard Failed", unchecked((int)0x800401D0));
        }
    }
}
