using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

/// <summary>
/// Two FileTransferService instances connected by real loopback EncryptedChannels,
/// each with a receive loop like BridgeSession's. The link can be cut and
/// re-established with the same device identities, as after a Wi-Fi drop.
/// </summary>
internal sealed class Link : IAsyncDisposable
{
    public required EncryptedChannel Sender { get; init; }   // sender side's channel
    public required EncryptedChannel Receiver { get; init; } // receiver side's channel
    private readonly List<Task> _loops = [];
    public int ChunksReceived;

    public static async Task<Link> ConnectAsync(DeviceIdentity senderId, FileTransferService senderSvc, string senderDir,
        DeviceIdentity receiverId, FileTransferService receiverSvc, string receiverDir,
        Func<BridgeMessage, bool>? dropAtSender = null, Action<BridgeMessage>? onReceiver = null)
    {
        var (client, server) = await Loopback.ConnectAsync();
        var respTask = EncryptedChannel.HandshakeAsResponderAsync(server, receiverId);
        var s = await EncryptedChannel.HandshakeAsInitiatorAsync(client, senderId, receiverId.PublicKeyBlob);
        var r = await respTask;
        r.MarkPeerAuthenticated();
        var link = new Link { Sender = s, Receiver = r };
        link._loops.Add(Pump(s, senderSvc, senderDir, dropAtSender, null));
        link._loops.Add(Pump(r, receiverSvc, receiverDir, null, m => { if (m.Type == MessageType.FileChunk) Interlocked.Increment(ref link.ChunksReceived); onReceiver?.Invoke(m); }));
        return link;
    }

    private static Task Pump(EncryptedChannel ch, FileTransferService svc, string dir, Func<BridgeMessage, bool>? drop, Action<BridgeMessage>? seen) => Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                var m = await ch.ReceiveAsync();
                seen?.Invoke(m);
                if (drop?.Invoke(m) == true) continue;
                await svc.HandleIncomingAsync(ch, m, dir);
            }
        }
        catch (Exception) { }
        finally { svc.AbortAll(ch); }
    });

    /// <summary>Simulates the connection dropping.</summary>
    public async Task CutAsync()
    {
        await Sender.DisposeAsync();
        await Receiver.DisposeAsync();
        await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async ValueTask DisposeAsync() => await CutAsync();
}

public sealed class ResumeAndFolderTests : IDisposable
{
    private readonly DeviceIdentity _phone = DeviceIdentity.Generate();
    private readonly DeviceIdentity _pc = DeviceIdentity.Generate();
    private readonly string _src = Loopback.TempDir();
    private readonly string _dl = Loopback.TempDir();
    private readonly string _senderState = Loopback.TempDir();
    private readonly string _receiverState = Loopback.TempDir();

    public void Dispose()
    {
        foreach (var d in new[] { _src, _dl, _senderState, _receiverState }) try { Directory.Delete(d, true); } catch { }
    }

    private SendOptions ToPc(bool resume = true, bool folders = true) => new(_pc.DeviceId, resume, folders);

    private string MakeFile(string name, int chunks, int extra = 123, int seed = 1)
    {
        var bytes = new byte[FileTransferService.ChunkSize * chunks + extra];
        new Random(seed).NextBytes(bytes);
        var path = Path.Combine(_src, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Starts a send and cuts the link once the receiver has about <paramref name="cutAfterChunks"/> chunks.</summary>
    private async Task<TransferProgress> SendAndCutAsync(FileTransferService sender, FileTransferService receiver, string path, int cutAfterChunks)
    {
        var cut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = await Link.ConnectAsync(_phone, sender, _src, _pc, receiver, _dl,
            onReceiver: m => { if (m.Type == MessageType.FileChunk && m.PayloadAs<FileChunkPayload>().Index + 1 >= cutAfterChunks) cut.TrySetResult(); });
        var send = sender.SendFileAsync(link.Sender, path, ToPc());
        await cut.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await link.CutAsync();
        return await send.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Interrupted_Transfer_Resumes_From_The_Last_Chunk_After_Reconnect()
    {
        var sender = new FileTransferService(new TransferStateStore(_senderState));
        var receiver = new FileTransferService(new TransferStateStore(_receiverState));
        var path = MakeFile("video.mp4", 60);
        var receiverEvents = new List<TransferProgress>();
        receiver.ProgressChanged += p => { lock (receiverEvents) receiverEvents.Add(p); };

        var first = await SendAndCutAsync(sender, receiver, path, cutAfterChunks: 20);
        Assert.Equal(TransferStatus.Interrupted, first.Status);
        Assert.Single(sender.State.PendingFor(_pc.DeviceId));
        Assert.Contains(receiverEvents, p => p.Status == TransferStatus.Interrupted);
        Assert.Single(Directory.GetFiles(Path.Combine(_dl, FileTransferService.PartialDirName), "*.part")); // partial kept
        Assert.Empty(Directory.GetFiles(_dl)); // nothing half-finished in Downloads itself

        // Reconnect: the sender offers it again and continues from where the receiver stopped.
        var done = new TaskCompletionSource<TransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.ProgressChanged += p => { if (p.Status == TransferStatus.Completed) done.TrySetResult(p); };
        TransferProgress? resumedSend = null;
        sender.ProgressChanged += p => { if (p.ResumedFrom > 0) resumedSend = p; };
        await using var link2 = await Link.ConnectAsync(_phone, sender, _src, _pc, receiver, _dl);
        await sender.ResumePendingAsync(link2.Sender, ToPc());
        var received = await done.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.NotNull(resumedSend);
        Assert.True(resumedSend!.ResumedFrom >= 19, $"resumed from {resumedSend.ResumedFrom}");
        Assert.True(link2.ChunksReceived <= 61 - resumedSend.ResumedFrom, $"re-sent {link2.ChunksReceived} chunks");
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(received.SavedPath!));
        Assert.Equal(Path.Combine(_dl, "video.mp4"), received.SavedPath);
        await Task.Delay(200); // let the ack land
        Assert.Empty(sender.State.PendingFor(_pc.DeviceId));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dl, FileTransferService.PartialDirName)));
    }

    [Fact]
    public async Task Resume_Survives_App_Restart_On_Both_Sides()
    {
        var path = MakeFile("big.bin", 40);
        var oldSenderState = new TransferStateStore(_senderState);
        var oldReceiverState = new TransferStateStore(_receiverState);
        var first = await SendAndCutAsync(new FileTransferService(oldSenderState), new FileTransferService(oldReceiverState), path, 10);
        Assert.Equal(TransferStatus.Interrupted, first.Status);
        oldSenderState.Dispose(); // the apps exit (MainWindow.ShutdownAsync / service onDestroy flush their state)
        oldReceiverState.Dispose();

        // Fresh service instances, as after closing and reopening both apps.
        var sender = new FileTransferService(new TransferStateStore(_senderState));
        var receiver = new FileTransferService(new TransferStateStore(_receiverState));
        var done = new TaskCompletionSource<TransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.ProgressChanged += p => { if (p.Status == TransferStatus.Completed) done.TrySetResult(p); };
        await using var link = await Link.ConnectAsync(_phone, sender, _src, _pc, receiver, _dl);
        await sender.ResumePendingAsync(link.Sender, ToPc());
        var received = await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(received.ResumedFrom >= 9);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(received.SavedPath!));
    }

    [Fact]
    public async Task Lost_Ack_Does_Not_Store_The_File_Twice()
    {
        var sender = new FileTransferService(new TransferStateStore(_senderState));
        var receiver = new FileTransferService(new TransferStateStore(_receiverState));
        var path = MakeFile("doc.pdf", 3);

        // The receiver stores the file and acks, but the ack never reaches the sender.
        var stored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.ProgressChanged += p => { if (p.Status == TransferStatus.Completed) stored.TrySetResult(); };
        var link = await Link.ConnectAsync(_phone, sender, _src, _pc, receiver, _dl, dropAtSender: m => m.Type == MessageType.FileAck);
        var send = sender.SendFileAsync(link.Sender, path, ToPc());
        await stored.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await link.CutAsync();
        Assert.Equal(TransferStatus.Interrupted, (await send.WithTimeout()).Status); // never confirmed: not "sent"
        Assert.Single(sender.State.PendingFor(_pc.DeviceId));

        await using var link2 = await Link.ConnectAsync(_phone, sender, _src, _pc, receiver, _dl);
        await sender.ResumePendingAsync(link2.Sender, ToPc());
        await Task.Delay(300);

        Assert.Empty(sender.State.PendingFor(_pc.DeviceId));
        Assert.Equal(0, link2.ChunksReceived); // nothing re-sent
        Assert.Single(Directory.GetFiles(_dl)); // not "doc (1).pdf"
    }

    [Fact]
    public async Task Changed_Source_File_Restarts_From_Zero()
    {
        var sender = new FileTransferService(new TransferStateStore(_senderState));
        var receiver = new FileTransferService(new TransferStateStore(_receiverState));
        var path = MakeFile("notes.bin", 30, seed: 1);
        var first = await SendAndCutAsync(sender, receiver, path, 10);
        Assert.Equal(TransferStatus.Interrupted, first.Status);

        MakeFile("notes.bin", 30, seed: 2); // same size, different content
        var done = new TaskCompletionSource<TransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.ProgressChanged += p => { if (p.Status == TransferStatus.Completed) done.TrySetResult(p); };
        await using var link = await Link.ConnectAsync(_phone, sender, _src, _pc, receiver, _dl);
        await sender.ResumePendingAsync(link.Sender, ToPc());
        var received = await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, received.ResumedFrom);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(received.SavedPath!));
    }

    [Fact]
    public async Task Another_Device_Cannot_Pick_Up_Someone_Elses_Partial()
    {
        var sender = new FileTransferService(new TransferStateStore(_senderState));
        var receiver = new FileTransferService(new TransferStateStore(_receiverState));
        var path = MakeFile("secret.bin", 30);
        var first = await SendAndCutAsync(sender, receiver, path, 15);
        var transferId = first.TransferId;

        // A different paired phone offers the same transfer id with identical metadata.
        var other = DeviceIdentity.Generate();
        var meta = new FileStartPayload(transferId, "secret.bin", new FileInfo(path).Length, await FileHasher.Sha256HexAsync(path), 31) { Resumable = true };
        var nextIndex = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (client, server) = await Loopback.ConnectAsync();
        var respTask = EncryptedChannel.HandshakeAsResponderAsync(server, _pc);
        await using var attacker = await EncryptedChannel.HandshakeAsInitiatorAsync(client, other, _pc.PublicKeyBlob);
        await using var pcSide = await respTask;
        await attacker.SendAsync(BridgeMessage.Create(MessageType.FileStart, meta));
        await receiver.HandleIncomingAsync(pcSide, await pcSide.ReceiveAsync(), _dl);
        var reply = await attacker.ReceiveAsync().WithTimeout();
        Assert.Equal(MessageType.FileResume, reply.Type);
        Assert.Equal(0, reply.PayloadAs<FileResumePayload>().NextIndex); // starts from scratch, sees none of the original
        receiver.AbortAll(pcSide);
    }

    [Fact]
    public async Task User_Cancel_Of_An_Interrupted_Send_Drops_It_From_The_Queue()
    {
        var sender = new FileTransferService(new TransferStateStore(_senderState));
        var receiver = new FileTransferService(new TransferStateStore(_receiverState));
        var first = await SendAndCutAsync(sender, receiver, MakeFile("x.bin", 30), 5);
        sender.CancelTransfer(first.TransferId);
        Assert.Empty(sender.State.PendingFor(_pc.DeviceId));

        await using var link = await Link.ConnectAsync(_phone, sender, _src, _pc, receiver, _dl);
        await sender.ResumePendingAsync(link.Sender, ToPc());
        await Task.Delay(200);
        Assert.Equal(0, link.ChunksReceived);
    }

    [Fact]
    public void Expired_Partials_Are_Cleaned_Up()
    {
        var partialDir = Path.Combine(_dl, FileTransferService.PartialDirName);
        Directory.CreateDirectory(partialDir);
        var oldPart = Path.Combine(partialDir, "aaaa.part");
        var oldMeta = Path.Combine(partialDir, "aaaa.json");
        var newPart = Path.Combine(partialDir, "bbbb.part");
        File.WriteAllText(oldPart, "x"); File.WriteAllText(oldMeta, "{}"); File.WriteAllText(newPart, "y");
        File.SetLastWriteTimeUtc(oldMeta, DateTime.UtcNow.AddHours(-25));

        FileTransferService.CleanupExpiredPartials(_dl);
        Assert.False(File.Exists(oldPart));
        Assert.False(File.Exists(oldMeta));
        Assert.True(File.Exists(newPart));
    }

    [Fact]
    public async Task Resume_Index_Out_Of_Range_Fails_The_Send_Cleanly()
    {
        // A buggy or hostile receiver answers file.start with a nonsense index.
        var sender = new FileTransferService(new TransferStateStore(_senderState));
        var path = MakeFile("a.bin", 2);
        var (client, server) = await Loopback.ConnectAsync();
        var respTask = EncryptedChannel.HandshakeAsResponderAsync(server, _pc);
        await using var s = await EncryptedChannel.HandshakeAsInitiatorAsync(client, _phone, _pc.PublicKeyBlob);
        await using var r = await respTask;
        var pump = Task.Run(async () => { try { while (true) await sender.HandleIncomingAsync(s, await s.ReceiveAsync(), _src); } catch { } });

        var send = sender.SendFileAsync(s, path, ToPc());
        var start = (await r.ReceiveAsync().WithTimeout()).PayloadAs<FileStartPayload>();
        Assert.True(start.Resumable == true);
        await r.SendAsync(BridgeMessage.Create(MessageType.FileResume, new FileResumePayload(start.TransferId, 999)));
        var result = await send.WithTimeout();
        Assert.Equal(TransferStatus.Failed, result.Status);
        Assert.Empty(sender.State.PendingFor(_pc.DeviceId));
    }

    // ---------------------------------------------------------------- folders

    private string MakeTree()
    {
        var root = Path.Combine(_src, "Holiday");
        Directory.CreateDirectory(Path.Combine(root, "day 1", "raw"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        File.WriteAllText(Path.Combine(root, "readme.txt"), "top");
        File.WriteAllText(Path.Combine(root, "day 1", "a.jpg"), "aaa");
        File.WriteAllText(Path.Combine(root, "day 1", "raw", "b.raw"), "bbb");
        return root;
    }

    private async Task<List<TransferProgress>> ReceiveFolderAsync(FileTransferService sender, FileTransferService receiver, string folder, int expected)
    {
        var got = new List<TransferProgress>();
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(TransferProgress p) { if (p.Status == TransferStatus.Completed) lock (got) { got.Add(p); if (got.Count == expected) all.TrySetResult(); } }
        receiver.ProgressChanged += On;
        await using var link = await Link.ConnectAsync(_phone, sender, _src, _pc, receiver, _dl);
        var results = await sender.SendFolderAsync(link.Sender, folder, ToPc());
        Assert.All(results, r => Assert.Equal(TransferStatus.Completed, r.Status));
        await all.Task.WaitAsync(TimeSpan.FromSeconds(10));
        receiver.ProgressChanged -= On;
        return got;
    }

    [Fact]
    public async Task Folder_Arrives_With_Its_Structure()
    {
        var sender = new FileTransferService();
        var receiver = new FileTransferService();
        var got = await ReceiveFolderAsync(sender, receiver, MakeTree(), 3);

        Assert.Equal("top", File.ReadAllText(Path.Combine(_dl, "Holiday", "readme.txt")));
        Assert.Equal("aaa", File.ReadAllText(Path.Combine(_dl, "Holiday", "day 1", "a.jpg")));
        Assert.Equal("bbb", File.ReadAllText(Path.Combine(_dl, "Holiday", "day 1", "raw", "b.raw")));
        Assert.All(got, p => Assert.Equal(3, p.BatchCount));
        Assert.Equal(new[] { 0, 1, 2 }, got.Select(p => p.BatchIndex!.Value).OrderBy(i => i).ToArray());
    }

    [Fact]
    public async Task Same_Folder_Sent_Twice_Lands_Beside_Not_Inside()
    {
        var sender = new FileTransferService();
        var receiver = new FileTransferService();
        var tree = MakeTree();
        await ReceiveFolderAsync(sender, receiver, tree, 3);
        await ReceiveFolderAsync(sender, receiver, tree, 3);
        Assert.True(File.Exists(Path.Combine(_dl, "Holiday", "readme.txt")));
        Assert.True(File.Exists(Path.Combine(_dl, "Holiday (1)", "day 1", "raw", "b.raw")));
        Assert.False(File.Exists(Path.Combine(_dl, "Holiday", "readme (1).txt"))); // batches never mix
    }

    [Fact]
    public async Task Peer_Without_Folder_Support_Gets_Loose_Files()
    {
        var sender = new FileTransferService();
        var receiver = new FileTransferService();
        var got = new List<string>();
        receiver.ProgressChanged += p => { if (p.Status == TransferStatus.Completed) lock (got) got.Add(p.SavedPath!); };
        await using var link = await Link.ConnectAsync(_phone, sender, _src, _pc, receiver, _dl);
        var results = await sender.SendFolderAsync(link.Sender, MakeTree(), ToPc(resume: false, folders: false));
        Assert.All(results, r => Assert.Equal(TransferStatus.Completed, r.Status));
        await Task.Delay(300);
        Assert.Equal(3, got.Count);
        Assert.All(got, p => Assert.Equal(_dl, Path.GetDirectoryName(p)));
    }

    [Fact]
    public async Task Hostile_Folder_Paths_Stay_Inside_Downloads()
    {
        // Downloads sits inside its own parent so an escape would be visible (and nothing else is there).
        var dl = Path.Combine(_dl, "Bridge");
        Directory.CreateDirectory(dl);
        var receiver = new FileTransferService();
        var (client, server) = await Loopback.ConnectAsync();
        var respTask = EncryptedChannel.HandshakeAsResponderAsync(server, _pc);
        await using var phone = await EncryptedChannel.HandshakeAsInitiatorAsync(client, _phone, _pc.PublicKeyBlob);
        await using var pc = await respTask;
        var acks = new List<string>();

        async Task Offer(string id, string rel)
        {
            var data = "evil"u8.ToArray();
            await phone.SendAsync(BridgeMessage.Create(MessageType.FileStart,
                new FileStartPayload(id, "x.txt", data.Length, FileHasher.Sha256Hex(data), 1) { RelativePath = rel, BatchId = id }));
            await phone.SendAsync(BridgeMessage.Create(MessageType.FileChunk, new FileChunkPayload(id, 0, Convert.ToBase64String(data))));
            await phone.SendAsync(BridgeMessage.Create(MessageType.FileEnd, new FileEndPayload(id)));
            for (var i = 0; i < 3; i++) await receiver.HandleIncomingAsync(pc, await pc.ReceiveAsync(), dl);
            BridgeMessage m;
            while ((m = await phone.ReceiveAsync().WithTimeout()).Type != MessageType.FileAck) { }
            acks.Add(m.PayloadAs<FileAckPayload>().Status);
        }

        await Offer("1", "../../../outside");
        await Offer("2", "/etc/cron.d");
        await Offer("3", "C:/Windows/System32");
        await Offer("4", "a/../../b");
        await Offer("5", string.Join("/", Enumerable.Repeat("d", 40))); // too deep: refused

        var full = Path.GetFullPath(dl) + Path.DirectorySeparatorChar;
        foreach (var f in Directory.GetFiles(_dl, "x.txt", SearchOption.AllDirectories))
            Assert.StartsWith(full, Path.GetFullPath(f));
        Assert.Equal(new[] { "ok", "ok", "ok", "ok", "cancelled" }, acks.ToArray());
        Assert.True(File.Exists(Path.Combine(dl, "outside", "x.txt")));
    }

    [Fact]
    public async Task Interrupted_Folder_Resumes_Into_The_Same_Folder()
    {
        var sender = new FileTransferService(new TransferStateStore(_senderState));
        var receiver = new FileTransferService(new TransferStateStore(_receiverState));
        var root = Path.Combine(_src, "Album");
        for (var i = 0; i < 6; i++) MakeFile(Path.Combine("Album", $"p{i}.jpg"), 8, seed: i);

        // Cut the link when the third file is announced. The receiver is held there until the
        // cut, so the cut always lands mid-folder (a fixed delay raced a fast loopback link).
        var cut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var link = await Link.ConnectAsync(_phone, sender, _src, _pc, receiver, _dl,
            onReceiver: m =>
            {
                if (m.Type != MessageType.FileStart || m.PayloadAs<FileStartPayload>().BatchIndex != 2) return;
                cut.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
            });
        var sending = sender.SendFolderAsync(link.Sender, root, ToPc());
        await cut.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cutting = link.CutAsync(); // the sender's side goes first, so it never hears back about file 3
        release.Set();
        await cutting;
        var partial = await sending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(partial, r => r.Status == TransferStatus.Interrupted);
        var completedBefore = partial.Count(r => r.Status == TransferStatus.Completed);
        Assert.True(completedBefore >= 2);
        // Everything not confirmed yet is queued: the interrupted file and the ones not started.
        Assert.Equal(6 - completedBefore, sender.State.PendingFor(_pc.DeviceId).Count);

        var done = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.ProgressChanged += p => { if (p.Status == TransferStatus.Completed && Interlocked.Increment(ref done) == 6 - completedBefore) all.TrySetResult(); };
        await using var link2 = await Link.ConnectAsync(_phone, sender, _src, _pc, receiver, _dl);
        await sender.ResumePendingAsync(link2.Sender, ToPc());
        await all.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(6, Directory.GetFiles(Path.Combine(_dl, "Album")).Length);
        Assert.False(Directory.Exists(Path.Combine(_dl, "Album (1)")));
        for (var i = 0; i < 6; i++)
            Assert.Equal(File.ReadAllBytes(Path.Combine(root, $"p{i}.jpg")), File.ReadAllBytes(Path.Combine(_dl, "Album", $"p{i}.jpg")));
    }
}
