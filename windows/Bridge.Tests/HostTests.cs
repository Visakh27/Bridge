using Bridge.Core;
using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

/// <summary>
/// End-to-end over loopback TCP: a simulated phone pairs with a real
/// BridgeHost, reconnects, and exchanges files, clipboard and status.
/// </summary>
public sealed class HostTests : IDisposable
{
    private readonly string _downloads = Loopback.TempDir();
    private readonly DeviceIdentity _pc = DeviceIdentity.Generate();
    private readonly InMemoryPairedDeviceStore _store = new();
    private readonly FakeClipboard _pcClipboard = new();
    private readonly TcpBridgeListener _listener = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly BridgeHost _host;
    private readonly PairingService _pairing;
    private readonly ClipboardSyncService _clipboard;
    private readonly FileTransferService _files = new();

    public HostTests()
    {
        _pairing = new PairingService(_pc, _store, "Test PC");
        _clipboard = new ClipboardSyncService(_pcClipboard) { Enabled = true };
        _host = new BridgeHost(_pc, _store, _pairing, _files, _clipboard, _downloads);
        _ = _host.RunAsync(_listener, _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _host.DisposeAsync().AsTask().Wait();
        _listener.Dispose();
        try { Directory.Delete(_downloads, recursive: true); } catch { }
    }

    private async Task<EncryptedChannel> ConnectAsync(DeviceIdentity phone)
    {
        var transport = await TcpTransport.ConnectAsync("127.0.0.1", _listener.Port);
        return await EncryptedChannel.HandshakeAsInitiatorAsync(transport, phone, _pc.PublicKeyBlob).WithTimeout();
    }

    private async Task PairAsync(DeviceIdentity phone)
    {
        var qr = _pairing.CreateQrPayload(_listener.Port, "127.0.0.1");
        await using var ch = await ConnectAsync(phone);
        await ch.SendAsync(BridgeMessage.Create(MessageType.PairingRequest,
            new PairingRequestPayload(qr.PairingToken, "Pixel", Convert.ToBase64String(phone.PublicKeyBlob))));
        Assert.Equal(MessageType.PairingAccept, (await ch.ReceiveAsync().WithTimeout()).Type);
    }

    private async Task<(EncryptedChannel Channel, BridgeSession Session)> StartSessionAsync(DeviceIdentity phone)
    {
        var started = new TaskCompletionSource<BridgeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.SessionStarted += s => started.TrySetResult(s);
        var ch = await ConnectAsync(phone);
        await ch.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise,
            new CapabilitiesPayload([Features.Files, Features.Clipboard, Features.Notifications])));
        var reply = await ch.ReceiveAsync().WithTimeout();
        Assert.Equal(MessageType.CapabilitiesAdvertise, reply.Type);
        Assert.Contains(Features.Clipboard, string.Join(",", reply.PayloadAs<CapabilitiesPayload>().Features));
        return (ch, await started.Task.WithTimeout());
    }

    [Fact]
    public async Task Pair_Then_Reconnect_Starts_Authenticated_Session()
    {
        var phone = DeviceIdentity.Generate();
        await PairAsync(phone);
        var (ch, session) = await StartSessionAsync(phone);
        await using (ch)
        {
            Assert.Equal(phone.DeviceId, session.Device.DeviceId);
            Assert.Equal("Pixel", session.Device.DeviceName);
            Assert.True(session.Device.IsConnected);

            var statusSeen = new TaskCompletionSource<ConnectedDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.StatusChanged += d => { if (d.BatteryPercent == 42) statusSeen.TrySetResult(d); };
            await ch.SendAsync(BridgeMessage.Create(MessageType.DeviceStatus, new DeviceStatusPayload(42, true, "good")));
            Assert.True((await statusSeen.Task.WithTimeout()).Charging);

            await ch.SendAsync(BridgeMessage.CreateEmpty(MessageType.PresencePing));
            Assert.Equal(MessageType.PresencePong, (await ch.ReceiveAsync().WithTimeout()).Type);
        }
    }

    [Fact]
    public async Task Unpaired_Device_Is_Refused()
    {
        await using var ch = await ConnectAsync(DeviceIdentity.Generate());
        await ch.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload([Features.Files])));
        var reply = await ch.ReceiveAsync().WithTimeout();
        Assert.Equal(MessageType.Error, reply.Type);
        Assert.Equal(ErrorCodes.DeviceUnpaired, reply.PayloadAs<ErrorPayload>().Code);
        Assert.Null(_host.CurrentSession);
    }

    [Fact]
    public async Task Spoofed_Device_Id_Is_Refused()
    {
        var phone = DeviceIdentity.Generate();
        await PairAsync(phone);
        var attacker = DeviceIdentity.Generate(phone.DeviceId); // knows the id, not the key
        await using var ch = await ConnectAsync(attacker);
        await ch.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload([Features.Files])));
        var reply = await ch.ReceiveAsync().WithTimeout();
        Assert.Equal(ErrorCodes.AuthFailed, reply.PayloadAs<ErrorPayload>().Code);
        Assert.Null(_host.CurrentSession);
    }

    [Fact]
    public async Task Stalled_Client_Does_Not_Block_Others()
    {
        // Connect and say nothing: the old accept loop would wait on this forever.
        await using var idle = await TcpTransport.ConnectAsync("127.0.0.1", _listener.Port);
        var phone = DeviceIdentity.Generate();
        await PairAsync(phone).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Phone_To_PC_File_Lands_In_Downloads_With_Safe_Name()
    {
        var phone = DeviceIdentity.Generate();
        await PairAsync(phone);
        var (ch, session) = await StartSessionAsync(phone);
        await using (ch)
        {
            var done = new TaskCompletionSource<TransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
            _files.ProgressChanged += p => { if (p.Direction == TransferDirection.Receiving && p.Status != TransferStatus.InProgress) done.TrySetResult(p); };

            var content = new byte[FileTransferService.ChunkSize * 2 + 123];
            Random.Shared.NextBytes(content);
            var src = Path.Combine(Loopback.TempDir(), "report.bin");
            await File.WriteAllBytesAsync(src, content);

            // Send under a hostile name by crafting the messages the way a malicious peer would.
            var hash = await FileHasher.Sha256HexAsync(src);
            const string id = "t-1";
            await ch.SendAsync(BridgeMessage.Create(MessageType.FileStart, new FileStartPayload(id, "../../outside.bin", content.Length, hash, 3)));
            for (var i = 0; i < 3; i++)
            {
                var slice = content.Skip(i * FileTransferService.ChunkSize).Take(FileTransferService.ChunkSize).ToArray();
                await ch.SendAsync(BridgeMessage.Create(MessageType.FileChunk, new FileChunkPayload(id, i, Convert.ToBase64String(slice))));
            }
            await ch.SendAsync(BridgeMessage.Create(MessageType.FileEnd, new FileEndPayload(id)));

            var ack = await ch.ReceiveAsync().WithTimeout();
            Assert.Equal(MessageType.FileAck, ack.Type);
            Assert.Equal(FileAckStatus.Ok, ack.PayloadAs<FileAckPayload>().Status);

            var result = await done.Task.WithTimeout();
            Assert.Equal(TransferStatus.Completed, result.Status);
            Assert.Equal(Path.Combine(_downloads, "outside.bin"), result.SavedPath);
            Assert.Equal(content, await File.ReadAllBytesAsync(result.SavedPath!));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(_downloads))!, "outside.bin")));
            Assert.Equal(0, Directory.GetFiles(_downloads, "*.part").Length);
        }
    }

    [Fact]
    public async Task Out_Of_Order_Chunk_Fails_Transfer()
    {
        var phone = DeviceIdentity.Generate();
        await PairAsync(phone);
        var (ch, _) = await StartSessionAsync(phone);
        await using (ch)
        {
            await ch.SendAsync(BridgeMessage.Create(MessageType.FileStart, new FileStartPayload("t-2", "a.bin", 70_000, "00", 2)));
            await ch.SendAsync(BridgeMessage.Create(MessageType.FileChunk, new FileChunkPayload("t-2", 1, Convert.ToBase64String(new byte[5]))));
            var ack = await ch.ReceiveAsync().WithTimeout();
            Assert.Equal(FileAckStatus.HashMismatch, ack.PayloadAs<FileAckPayload>().Status);
            Assert.Equal(0, Directory.GetFiles(_downloads).Length);
        }
    }

    [Fact]
    public async Task PC_To_Phone_File_And_Clipboard_Both_Ways()
    {
        var phone = DeviceIdentity.Generate();
        await PairAsync(phone);
        var (ch, session) = await StartSessionAsync(phone);
        await using (ch)
        {
            // Phone → PC clipboard.
            await ch.SendAsync(BridgeMessage.Create(MessageType.ClipboardUpdate, new ClipboardUpdatePayload("text", "from phone")));
            await _pcClipboard.Written.Task.WithTimeout();
            Assert.Equal("from phone", _pcClipboard.Text);

            // The resulting local change must not echo back; a new copy must be sent.
            _pcClipboard.SimulateLocalCopy("from phone");
            _pcClipboard.SimulateLocalCopy("from pc");
            var clip = await ch.ReceiveAsync().WithTimeout();
            Assert.Equal(MessageType.ClipboardUpdate, clip.Type);
            Assert.Equal("from pc", clip.PayloadAs<ClipboardUpdatePayload>().Data);

            // PC → phone file.
            var src = Path.Combine(Loopback.TempDir(), "notes.txt");
            await File.WriteAllTextAsync(src, "hello phone");
            var send = session.SendFileAsync(src);
            var start = await ch.ReceiveAsync().WithTimeout();
            var meta = start.PayloadAs<FileStartPayload>();
            Assert.Equal("notes.txt", meta.FileName);
            Assert.Equal(1, meta.TotalChunks);
            var chunk = (await ch.ReceiveAsync().WithTimeout()).PayloadAs<FileChunkPayload>();
            Assert.Equal("hello phone", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(chunk.Data)));
            Assert.Equal(MessageType.FileEnd, (await ch.ReceiveAsync().WithTimeout()).Type);
            Assert.Equal(TransferStatus.Completed, (await send.WithTimeout()).Status);
        }
    }

    [Fact]
    public async Task Revoke_Drops_Session_And_Blocks_Reconnect()
    {
        var phone = DeviceIdentity.Generate();
        await PairAsync(phone);
        var (ch, session) = await StartSessionAsync(phone);
        await using (ch)
        {
            await _host.RevokeAsync(phone.DeviceId);
            Assert.False(session.IsRunning);
            await Assert.ThrowsAnyAsync<Exception>(async () => await ch.ReceiveAsync().WithTimeout());
        }
        await using var again = await ConnectAsync(phone);
        await again.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload([])));
        Assert.Equal(ErrorCodes.DeviceUnpaired, (await again.ReceiveAsync().WithTimeout()).PayloadAs<ErrorPayload>().Code);
    }

    [Fact]
    public async Task Receiver_Rejection_Stops_Sender_Early()
    {
        // A phone that refuses the file (e.g. storage full) must stop the PC
        // mid-stream, and the PC must not report "Completed".
        var phone = DeviceIdentity.Generate();
        await PairAsync(phone);
        var (ch, session) = await StartSessionAsync(phone);
        await using (ch)
        {
            var src = Path.Combine(Loopback.TempDir(), "big.bin");
            await File.WriteAllBytesAsync(src, new byte[FileTransferService.ChunkSize * 200]);
            var send = session.SendFileAsync(src);

            var start = await ch.ReceiveAsync().WithTimeout();
            var id = start.PayloadAs<FileStartPayload>().TransferId;
            await ch.SendAsync(BridgeMessage.Create(MessageType.FileAck, new FileAckPayload(id, FileAckStatus.Cancelled)));

            var result = await send.WithTimeout();
            Assert.Equal(TransferStatus.Cancelled, result.Status);
            Assert.True(result.ChunksDone < 200, $"sender kept going: {result.ChunksDone} chunks");

            // Drain: nothing after the rejection should be a file.end.
            var types = new List<string>();
            await ch.SendAsync(BridgeMessage.CreateEmpty(MessageType.PresencePing));
            while (true)
            {
                var m = await ch.ReceiveAsync().WithTimeout();
                if (m.Type == MessageType.PresencePong) break;
                types.Add(m.Type);
            }
            Assert.DoesNotContain(MessageType.FileEnd, string.Join(",", types));
        }
    }

    [Fact]
    public async Task Disconnect_Mid_Transfer_Leaves_No_Partial_File()
    {
        var phone = DeviceIdentity.Generate();
        await PairAsync(phone);
        var (ch, session) = await StartSessionAsync(phone);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Ended += _ => ended.TrySetResult();
        var failed = new TaskCompletionSource<TransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        _files.ProgressChanged += p => { if (p.Status == TransferStatus.Failed) failed.TrySetResult(p); };

        await ch.SendAsync(BridgeMessage.Create(MessageType.FileStart, new FileStartPayload("t-x", "half.bin", 100_000, "00", 2)));
        await ch.SendAsync(BridgeMessage.Create(MessageType.FileChunk, new FileChunkPayload("t-x", 0, Convert.ToBase64String(new byte[65536]))));
        await Task.Delay(300);
        Assert.Single(Directory.GetFiles(_downloads, "*.part"));
        await ch.DisposeAsync(); // phone walks out of Wi-Fi range

        await ended.Task.WithTimeout();
        Assert.Equal("half.bin", (await failed.Task.WithTimeout()).FileName);
        Assert.Empty(Directory.GetFiles(_downloads));
    }

    [Fact]
    public async Task Malformed_Message_Does_Not_Drop_Session()
    {
        var phone = DeviceIdentity.Generate();
        await PairAsync(phone);
        var (ch, session) = await StartSessionAsync(phone);
        await using (ch)
        {
            // file.start with the wrong field types, then an unknown future message type.
            await ch.SendAsync(new BridgeMessage { Type = MessageType.FileStart, Payload = new System.Text.Json.Nodes.JsonObject { ["transferId"] = 5, ["size"] = "big" } });
            await ch.SendAsync(BridgeMessage.Create("photos.list", new { Album = "x" }));
            await ch.SendAsync(BridgeMessage.CreateEmpty(MessageType.PresencePing));
            Assert.Equal(MessageType.PresencePong, (await ch.ReceiveAsync().WithTimeout()).Type);
            Assert.True(session.IsRunning);
        }
    }

    [Fact]
    public async Task Concurrent_Sends_Keep_Frames_In_Nonce_Order()
    {
        // Several files + clipboard sent at once over one channel: with counter
        // nonces, any interleaving bug between encrypt and send breaks decryption.
        var phone = DeviceIdentity.Generate();
        await PairAsync(phone);
        var (ch, session) = await StartSessionAsync(phone);
        await using (ch)
        {
            var dir = Loopback.TempDir();
            var sends = Enumerable.Range(0, 4).Select(i =>
            {
                var path = Path.Combine(dir, $"f{i}.bin");
                File.WriteAllBytes(path, Enumerable.Range(0, FileTransferService.ChunkSize * 3 + i).Select(b => (byte)(b + i)).ToArray());
                return Task.Run(() => session.SendFileAsync(path));
            }).ToList();
            for (var i = 0; i < 20; i++) _pcClipboard.SimulateLocalCopy($"clip {i}");

            int ends = 0, clips = 0;
            while (ends < 4 || clips < 20)
            {
                var m = await ch.ReceiveAsync().WithTimeout(); // throws if any frame is out of order
                if (m.Type == MessageType.FileEnd) ends++;
                if (m.Type == MessageType.ClipboardUpdate) clips++;
            }
            foreach (var s in sends) Assert.Equal(TransferStatus.Completed, (await s.WithTimeout()).Status);
        }
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; private set; }
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<string>? LocalClipboardChanged;
        public Task<string?> ReadTextAsync() => Task.FromResult(Text);
        public Task WriteTextAsync(string text) { Text = text; Written.TrySetResult(); return Task.CompletedTask; }
        public void SimulateLocalCopy(string text) { Text = text; LocalClipboardChanged?.Invoke(text); }
    }
}
