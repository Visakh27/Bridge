using System.Diagnostics;
using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

/// <summary>Phase B iteration 2: performance and hostile cases for resume.</summary>
public sealed class ResumeHardeningTests : IDisposable
{
    private readonly string _dir = Loopback.TempDir();
    private readonly DeviceIdentity _phone = DeviceIdentity.Generate();
    private readonly DeviceIdentity _pc = DeviceIdentity.Generate();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Large_Queue_Does_Not_Rewrite_The_State_File_Per_Item()
    {
        // The rest of an interrupted 5,000-file folder, then each file confirmed one by one.
        var store = new TransferStateStore(_dir);
        var entries = Enumerable.Range(0, 5000).Select(i => new TransferStateStore.OutboxEntry(
            $"t{i}", "pc", $"/photos/{i:D5}.jpg", $"{i:D5}.jpg", "Photos", "b", i, 5000, DateTimeOffset.UtcNow)).ToList();
        var sw = Stopwatch.StartNew();
        store.AddOutboxRange(entries);
        foreach (var e in entries) store.RemoveOutbox(e.TransferId);
        store.Flush();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed.TotalSeconds:F1} s");
        Assert.Empty(new TransferStateStore(_dir).PendingFor("pc")); // and the final state is on disk
    }

    [Fact]
    public void State_Changes_Reach_Disk_Shortly_After_Without_An_Explicit_Flush()
    {
        var store = new TransferStateStore(_dir);
        store.AddOutbox(new TransferStateStore.OutboxEntry("x", "pc", "/a", "a", null, null, null, null, DateTimeOffset.UtcNow));
        Thread.Sleep(TransferStateStore.FlushDelay + TimeSpan.FromMilliseconds(500));
        Assert.Single(new TransferStateStore(_dir).PendingFor("pc"));
    }

    [Fact]
    public void Completed_Record_Survives_The_Process_Being_Killed()
    {
        // No Flush/Dispose: as if Windows ended the process right after the file was stored.
        new TransferStateStore(_dir).MarkCompleted("phone", "t1", "/dl/a.jpg", "abc");
        File.AppendAllText(Path.Combine(_dir, "completed.log"), "{\"Key\":\"phone|t2\",\"Sav"); // torn last line
        var reopened = new TransferStateStore(_dir);
        Assert.Equal("/dl/a.jpg", reopened.CompletedPath("phone", "t1", "ABC"));
        Assert.Null(reopened.CompletedPath("phone", "t1", "other-hash"));
    }

    private async Task<(EncryptedChannel Phone, EncryptedChannel Pc)> PairAsync()
    {
        var (client, server) = await Loopback.ConnectAsync();
        var r = EncryptedChannel.HandshakeAsResponderAsync(server, _pc);
        var phone = await EncryptedChannel.HandshakeAsInitiatorAsync(client, _phone, _pc.PublicKeyBlob);
        return (phone, await r);
    }

    private static async Task<BridgeMessage> SendFileRaw(EncryptedChannel phone, EncryptedChannel pc, FileTransferService receiver, string dl, string id, byte[] data, bool resumable)
    {
        var total = (int)((data.Length + FileTransferService.ChunkSize - 1) / FileTransferService.ChunkSize);
        await phone.SendAsync(BridgeMessage.Create(MessageType.FileStart, new FileStartPayload(id, "f.bin", data.Length, FileHasher.Sha256Hex(data), total) { Resumable = resumable ? true : null }));
        await receiver.HandleIncomingAsync(pc, await pc.ReceiveAsync(), dl);
        var first = await phone.ReceiveAsync().WithTimeout();
        if (first.Type == MessageType.FileAck) return first; // answered without needing the data
        for (var i = first.PayloadAs<FileResumePayload>().NextIndex; i < total; i++)
        {
            var slice = data.Skip(i * FileTransferService.ChunkSize).Take(FileTransferService.ChunkSize).ToArray();
            await phone.SendAsync(BridgeMessage.Create(MessageType.FileChunk, new FileChunkPayload(id, i, Convert.ToBase64String(slice))));
            await receiver.HandleIncomingAsync(pc, await pc.ReceiveAsync(), dl);
        }
        await phone.SendAsync(BridgeMessage.Create(MessageType.FileEnd, new FileEndPayload(id)));
        await receiver.HandleIncomingAsync(pc, await pc.ReceiveAsync(), dl);
        return await phone.ReceiveAsync().WithTimeout();
    }

    [Fact]
    public async Task Reused_Transfer_Id_With_Different_Content_Is_Not_Answered_Ok_Unseen()
    {
        var receiver = new FileTransferService(new TransferStateStore(Path.Combine(_dir, "state")));
        var dl = Path.Combine(_dir, "dl");
        var (phone, pc) = await PairAsync();
        await using (phone) await using (pc)
        {
            Assert.Equal(FileAckStatus.Ok, (await SendFileRaw(phone, pc, receiver, dl, "same-id", "first"u8.ToArray(), true)).PayloadAs<FileAckPayload>().Status);
            // A buggy sender reuses the id for different content: it must actually be transferred.
            var ack = await SendFileRaw(phone, pc, receiver, dl, "same-id", "second version"u8.ToArray(), true);
            Assert.Equal(FileAckStatus.Ok, ack.PayloadAs<FileAckPayload>().Status);
            var texts = Directory.GetFiles(dl).Select(File.ReadAllText).OrderBy(t => t).ToArray();
            Assert.Equal(new[] { "first", "second version" }, texts);
        }
    }

    [Fact]
    public async Task Abandoned_Partials_From_One_Peer_Are_Capped()
    {
        var receiver = new FileTransferService();
        var dl = Path.Combine(_dir, "dl");
        for (var i = 0; i < FileTransferService.MaxPartialsPerPeer + 10; i++)
        {
            var (phone, pc) = await PairAsync();
            var data = new byte[FileTransferService.ChunkSize * 2];
            await phone.SendAsync(BridgeMessage.Create(MessageType.FileStart, new FileStartPayload($"p{i}", "f.bin", data.Length, FileHasher.Sha256Hex(data), 2) { Resumable = true }));
            await receiver.HandleIncomingAsync(pc, await pc.ReceiveAsync(), dl);
            receiver.AbortAll(pc); // the phone leaves and never comes back for it
            await phone.DisposeAsync(); await pc.DisposeAsync();
        }
        var partials = Directory.GetFiles(Path.Combine(dl, FileTransferService.PartialDirName), "*.part").Length;
        Assert.True(partials <= FileTransferService.MaxPartialsPerPeer, $"{partials} partials kept");
    }

    [Fact]
    public void Stale_Temp_Files_From_A_Crash_Are_Cleaned_Up()
    {
        var dl = Path.Combine(_dir, "dl");
        Directory.CreateDirectory(dl);
        var stale = Path.Combine(dl, $".bridge-{Guid.NewGuid():N}.part");
        var fresh = Path.Combine(dl, $".bridge-{Guid.NewGuid():N}.part");
        var userFile = Path.Combine(dl, "holiday.part"); // not ours: never touched
        File.WriteAllText(stale, "x"); File.WriteAllText(fresh, "y"); File.WriteAllText(userFile, "z");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-25));
        File.SetLastWriteTimeUtc(userFile, DateTime.UtcNow.AddHours(-25));
        FileTransferService.CleanupExpiredPartials(dl);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(userFile));
    }

    [Fact]
    public async Task Resume_Reuses_The_Hash_Of_An_Unchanged_File()
    {
        // Prove the cached hash is used: change the content but keep size and timestamp.
        // The receiver's end-to-end check then catches the mismatch (no silent corruption).
        var state = new TransferStateStore(Path.Combine(_dir, "sender"));
        var sender = new FileTransferService(state);
        var src = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(src, new byte[FileTransferService.ChunkSize * 3]);
        var stamp = File.GetLastWriteTimeUtc(src);
        var entry = new TransferStateStore.OutboxEntry("cached", _pc.DeviceId, src, "a.bin", null, null, null, null, DateTimeOffset.UtcNow);
        state.AddOutbox(entry with { Sha256 = FileHasher.Sha256Hex(new byte[FileTransferService.ChunkSize * 3]), Size = new FileInfo(src).Length, LastWriteUtc = stamp });

        var bytes = new byte[FileTransferService.ChunkSize * 3]; bytes[5] = 1;
        File.WriteAllBytes(src, bytes);
        File.SetLastWriteTimeUtc(src, stamp);

        var (phone, pc) = await PairAsync();
        await using (phone) await using (pc)
        {
            var pump = Task.Run(async () => { try { while (true) await sender.HandleIncomingAsync(phone, await phone.ReceiveAsync(), _dir); } catch { } });
            var resume = sender.ResumePendingAsync(phone, new SendOptions(_pc.DeviceId, true, true));
            var start = (await pc.ReceiveAsync().WithTimeout()).PayloadAs<FileStartPayload>();
            Assert.Equal(FileHasher.Sha256Hex(new byte[FileTransferService.ChunkSize * 3]), start.Sha256); // cached, not re-hashed
            await pc.SendAsync(BridgeMessage.Create(MessageType.FileAck, new FileAckPayload("cached", FileAckStatus.Cancelled)));
            await resume.WithTimeout();
        }

        // A changed timestamp or size forces a fresh hash.
        File.SetLastWriteTimeUtc(src, stamp.AddMinutes(1));
        state.AddOutbox(entry with { Sha256 = "stale", Size = bytes.Length, LastWriteUtc = stamp });
        var (phone2, pc2) = await PairAsync();
        await using (phone2) await using (pc2)
        {
            var pump = Task.Run(async () => { try { while (true) await sender.HandleIncomingAsync(phone2, await phone2.ReceiveAsync(), _dir); } catch { } });
            var resume = sender.ResumePendingAsync(phone2, new SendOptions(_pc.DeviceId, true, true));
            var start = (await pc2.ReceiveAsync().WithTimeout()).PayloadAs<FileStartPayload>();
            Assert.Equal(FileHasher.Sha256Hex(bytes), start.Sha256);
            await pc2.SendAsync(BridgeMessage.Create(MessageType.FileAck, new FileAckPayload("cached", FileAckStatus.Cancelled)));
            await resume.WithTimeout();
        }
    }
}
