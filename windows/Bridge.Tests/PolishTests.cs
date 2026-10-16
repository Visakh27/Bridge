using System.Text.RegularExpressions;
using Bridge.Core;
using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

/// <summary>Phase D on the PC: diagnostics log, settings, recent transfers.</summary>
public sealed class PolishTests
{
    private static readonly Regex LinePattern = new(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z (INFO|WARN) [a-z][a-z0-9_.]* ?([A-Za-z_][A-Za-z0-9_-]*=[^ ]+ ?)*$");

    // ------------------------------------------------------------------ diagnostics log

    [Fact]
    public void Only_Safe_Shapes_Reach_The_Log()
    {
        var log = new DiagnosticsLog(null);
        log.Info("transfer.done",
            LogField.Code("status", "Completed"),
            LogField.Code("name", "holiday photos.zip"),   // a file name: not a code word
            LogField.Code("name2", "report.pdf"),
            LogField.Code("text", new string('a', 40)),
            LogField.Code("nul", null),
            LogField.Id("peer", "phone-1234-secret-id", log),
            LogField.Num("chunks", 12),
            LogField.Flag("folder", true),
            LogField.Error(new IOException("C:\\Users\\ana\\secret.txt is locked")),
            LogField.Version("version", new Version(0, 2, 0)));
        log.Info("Not An Event!", LogField.Num("bad key", 1), LogField.Num("ok", 2));
        var lines = log.Lines();
        Assert.Equal(2, lines.Count);
        Assert.Contains("status=Completed", lines[0]);
        Assert.Contains("name=? ", lines[0]);
        Assert.Contains("name2=? ", lines[0]);
        Assert.Contains("text=? ", lines[0]);
        Assert.Contains("nul=? ", lines[0]);
        Assert.Matches(@"peer=[0-9a-f]{8} ", lines[0]);
        Assert.Contains("error=IOException", lines[0]);
        Assert.Contains("version=0.2.0", lines[0]);
        Assert.DoesNotContain("secret", lines[0]);
        Assert.DoesNotContain("holiday", lines[0]);
        Assert.Contains(" invalid_event ok=2", lines[1]);
        Assert.DoesNotContain("bad key", lines[1]);
        Assert.Equal(log.HashId("x"), log.HashId("x"));
        Assert.NotEqual(log.HashId("x"), new DiagnosticsLog(null).HashId("x")); // salted per run
    }

    [Fact]
    public void Log_Rotates_And_Never_Grows_Past_Its_Cap()
    {
        var dir = Loopback.TempDir();
        var log = new DiagnosticsLog(dir);
        for (var i = 0; i < 40_000; i++) log.Info("transfer.done", LogField.Num("i", i), LogField.Code("status", "Completed"));
        var files = log.LogFiles();
        Assert.True(files.Count <= DiagnosticsLog.Files, $"{files.Count} files");
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= DiagnosticsLog.MaxFileBytes, $"{f} too big"));
        Assert.Equal(DiagnosticsLog.Files, Directory.GetFiles(dir).Length);
        var lines = log.Lines();
        Assert.EndsWith("i=39999 status=Completed", lines[^1]); // newest kept
        Assert.All(lines, l => Assert.Matches(LinePattern, l));
    }

    [Fact]
    public void Old_Logs_Are_Deleted_And_Disabled_Means_Nothing_Is_Written()
    {
        var dir = Loopback.TempDir();
        File.WriteAllText(Path.Combine(dir, "bridge.1.log"), "old\n");
        File.SetLastWriteTimeUtc(Path.Combine(dir, "bridge.1.log"), DateTime.UtcNow.AddDays(-15));
        File.WriteAllText(Path.Combine(dir, "bridge.log"), "recent\n");
        var log = new DiagnosticsLog(dir);
        Assert.Equal(["recent"], log.Lines());

        log.Enabled = false;
        log.Info("app.started");
        Assert.Equal(["recent"], log.Lines());
        log.Clear();
        Assert.Empty(log.Lines());
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void Concurrent_Writers_Never_Interleave_Lines()
    {
        var log = new DiagnosticsLog(Loopback.TempDir());
        Parallel.For(0, 8, t => { for (var i = 0; i < 1500; i++) log.Info("transfer.done", LogField.Num("t", t), LogField.Num("i", i)); });
        var lines = log.Lines();
        Assert.All(lines, l => Assert.Matches(LinePattern, l));
        Assert.True(lines.Count > 1000);
    }

    [Fact]
    public void A_Log_Directory_That_Cannot_Be_Written_Does_Not_Throw()
    {
        var file = Path.Combine(Loopback.TempDir(), "not-a-dir");
        File.WriteAllText(file, "x");
        var log = new DiagnosticsLog(file); // a file, not a directory
        log.Info("app.started");
        Assert.Empty(log.Lines());
    }

    // ------------------------------------------------------------------ nothing private in the log, end to end

    [Fact]
    public async Task A_Real_Session_Logs_Events_But_No_Names_Contents_Or_Keys()
    {
        const string Secret = "ZEBRA";
        var pc = DeviceIdentity.Generate();
        var store = new InMemoryPairedDeviceStore();
        var pairing = new PairingService(pc, store, "PC-" + Secret);
        var files = new FileTransferService();
        var clipboardText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clipboard = new ClipboardSyncService(new RecordingClipboard(clipboardText)) { Enabled = true };
        var downloads = Loopback.TempDir();
        var host = new BridgeHost(pc, store, pairing, files, clipboard, downloads);
        var router = new NotificationActionRouter(host, sessionWait: TimeSpan.FromMilliseconds(200));
        var log = new DiagnosticsLog(Loopback.TempDir());
        _ = new BridgeDiagnostics(log, host, files, router);
        using var listener = new TcpBridgeListener(0);
        using var cts = new CancellationTokenSource();
        _ = host.RunAsync(listener, cts.Token);
        try
        {
            var phone = DeviceIdentity.Generate();
            var qr = pairing.CreateQrPayload(listener.Port, "127.0.0.1");
            await using (var ch = await Connect(listener, phone, pc))
            {
                await ch.SendAsync(BridgeMessage.Create(MessageType.PairingRequest,
                    new PairingRequestPayload(qr.PairingToken, "Phone-" + Secret, Convert.ToBase64String(phone.PublicKeyBlob))));
                await ch.ReceiveAsync().WithTimeout();
            }
            // A stranger is refused (logged as a code, not who it was).
            await using (var stranger = await Connect(listener, DeviceIdentity.Generate(), pc))
            {
                await stranger.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload([Features.Files])));
                await stranger.ReceiveAsync().WithTimeout();
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            files.ProgressChanged += p => { if (p.Status == TransferStatus.Completed) done.TrySetResult(); };
            await using (var ch = await Connect(listener, phone, pc))
            {
                await ch.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise,
                    new CapabilitiesPayload([Features.Files, Features.Clipboard, Features.Notifications, Features.NotificationsActions])));
                await ch.ReceiveAsync().WithTimeout();
                var content = System.Text.Encoding.UTF8.GetBytes("contents " + Secret);
                await ch.SendAsync(BridgeMessage.Create(MessageType.FileStart, new FileStartPayload("t-" + Secret, Secret + "-plan.pdf",
                    content.Length, FileHasher.Sha256Hex(content), 1)));
                await ch.SendAsync(BridgeMessage.Create(MessageType.FileChunk, new FileChunkPayload("t-" + Secret, 0, Convert.ToBase64String(content))));
                await ch.SendAsync(BridgeMessage.Create(MessageType.FileEnd, new FileEndPayload("t-" + Secret)));
                await done.Task.WithTimeout();
                await ch.SendAsync(BridgeMessage.Create(MessageType.ClipboardUpdate, new ClipboardUpdatePayload("text", "clip " + Secret)));
                await clipboardText.Task.WithTimeout();
                await ch.SendAsync(BridgeMessage.Create(MessageType.NotificationPost, new NotificationPostPayload("App" + Secret, "Title " + Secret, "Text " + Secret, "key-" + Secret)));
                await ch.SendAsync(BridgeMessage.CreateEmpty("unknown." + Secret));
                // A notification action the phone answers.
                var press = router.PressAsync("key-" + Secret, "0-abc");
                BridgeMessage req;
                do req = await ch.ReceiveAsync().WithTimeout(); while (req.Type != MessageType.NotificationAction);
                await ch.SendAsync(BridgeMessage.Create(MessageType.NotificationActionResult,
                    new NotificationActionResultPayload(req.PayloadAs<NotificationActionPayload>().RequestId, "ok")));
                Assert.Equal("ok", await press.WithTimeout());
                await ch.SendAsync(BridgeMessage.Create(MessageType.Error, new ErrorPayload("PERMISSION_DENIED", "message " + Secret)));
                await Task.Delay(200);
            }
            await Task.Delay(300);
        }
        finally
        {
            cts.Cancel();
            await host.DisposeAsync();
        }

        var text = string.Join("\n", log.Lines());
        foreach (var expected in new[] { "pair.accepted", "connection.rejected reason=unpaired_device", "session.started", "transfer.done",
                     "dir=Receiving status=Completed", "notification.action kind=press status=ok", "peer.error code=PERMISSION_DENIED", "session.ended" })
            Assert.Contains(expected, text);
        Assert.DoesNotContain(Secret, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(pc.PublicKeyBlob)[..20], text);
        Assert.All(log.Lines(), l => Assert.Matches(LinePattern, l));

        var report = DiagnosticsReport.Build(log, DateTimeOffset.UtcNow, LogField.Version("app_version", new Version(0, 2, 0)), LogField.Flag("packaged", false));
        Assert.StartsWith("Bridge diagnostics report", report);
        Assert.Contains("app_version=0.2.0", report);
        Assert.DoesNotContain(Secret, report, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<EncryptedChannel> Connect(TcpBridgeListener listener, DeviceIdentity phone, DeviceIdentity pc)
    {
        var t = await TcpTransport.ConnectAsync("127.0.0.1", listener.Port);
        return await EncryptedChannel.HandshakeAsInitiatorAsync(t, phone, pc.PublicKeyBlob).WithTimeout();
    }

    private sealed class RecordingClipboard(TaskCompletionSource<string> got) : IClipboardAccess
    {
        public event Action<string>? LocalClipboardChanged { add { } remove { } }
        public Task<string?> ReadTextAsync() => Task.FromResult<string?>(null);
        public Task WriteTextAsync(string text) { got.TrySetResult(text); return Task.CompletedTask; }
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public void Settings_Default_Persist_And_Survive_Corruption()
    {
        var path = Path.Combine(Loopback.TempDir(), "sub", "settings.json");
        var store = new SettingsStore(path);
        Assert.Equal(new BridgeSettings(), store.Current);
        Assert.True(store.Current.ShowPhoneNotifications && store.Current.Diagnostics && !store.Current.ClipboardSync && !store.Current.OnboardingDone);

        var changes = 0;
        store.Changed += _ => changes++;
        var folder = Path.Combine(Loopback.TempDir(), "Received");
        store.Update(s => s with { ClipboardSync = true, StartMinimized = true, DownloadFolder = folder, OnboardingDone = true });
        store.Update(s => s with { ClipboardSync = true }); // no change: no save, no event
        Assert.Equal(1, changes);
        Assert.False(File.Exists(path + ".tmp"));

        var reopened = new SettingsStore(path);
        Assert.Equal(store.Current, reopened.Current);
        Assert.Contains("\"clipboardSync\": true", File.ReadAllText(path));

        File.WriteAllText(path, "{ not json");
        Assert.Equal(new BridgeSettings(), new SettingsStore(path).Current);
        File.WriteAllText(path, "{\"clipboardSync\":true,\"someFutureSetting\":42,\"downloadFolder\":\"relative\\\\path\"}");
        var future = new SettingsStore(path).Current;
        Assert.True(future.ClipboardSync);
        Assert.Null(future.DownloadFolder); // invalid folder dropped on load
    }

    [Fact]
    public void Download_Folder_Must_Be_A_Local_Absolute_Path()
    {
        Assert.Null(SettingsStore.ValidFolder(null));
        Assert.Null(SettingsStore.ValidFolder("  "));
        Assert.Null(SettingsStore.ValidFolder("relative/dir"));
        Assert.Null(SettingsStore.ValidFolder(@"\\server\share\files"));
        Assert.Null(SettingsStore.ValidFolder("//server/share"));
        Assert.Null(SettingsStore.ValidFolder(@"C:\Users\..\Windows"));
        Assert.Null(SettingsStore.ValidFolder("C:\\a\u0001b"));
        Assert.Null(SettingsStore.ValidFolder("C:\\" + new string('a', 300)));
        Assert.Equal(@"C:\Users\ana\Received", SettingsStore.ValidFolder(@"C:\Users\ana\Received"));
        Assert.Equal("D:/Bridge", SettingsStore.ValidFolder("D:/Bridge"));
    }

    [Fact]
    public void Settings_In_An_Unwritable_Place_Still_Work_For_This_Run()
    {
        var blocker = Path.Combine(Loopback.TempDir(), "file");
        File.WriteAllText(blocker, "x");
        var store = new SettingsStore(Path.Combine(blocker, "settings.json")); // parent is a file
        store.Update(s => s with { ClipboardSync = true });
        Assert.True(store.Current.ClipboardSync);
    }

    [Fact]
    public async Task Changing_The_Download_Folder_Applies_To_The_Live_Session()
    {
        var pc = DeviceIdentity.Generate();
        var store = new InMemoryPairedDeviceStore();
        var pairing = new PairingService(pc, store, "PC");
        var files = new FileTransferService();
        var first = Loopback.TempDir();
        var second = Loopback.TempDir();
        var host = new BridgeHost(pc, store, pairing, files, new ClipboardSyncService(new RecordingClipboard(new())), first);
        using var listener = new TcpBridgeListener(0);
        using var cts = new CancellationTokenSource();
        _ = host.RunAsync(listener, cts.Token);
        try
        {
            var phone = DeviceIdentity.Generate();
            var qr = pairing.CreateQrPayload(listener.Port, "127.0.0.1");
            await using (var ch = await Connect(listener, phone, pc))
            {
                await ch.SendAsync(BridgeMessage.Create(MessageType.PairingRequest,
                    new PairingRequestPayload(qr.PairingToken, "P", Convert.ToBase64String(phone.PublicKeyBlob))));
                await ch.ReceiveAsync().WithTimeout();
            }
            await using var s = await Connect(listener, phone, pc);
            await s.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload([Features.Files])));
            await s.ReceiveAsync().WithTimeout();

            async Task<string> Send(string name)
            {
                var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                void On(TransferProgress p) { if (p.FileName == name && p.Status == TransferStatus.Completed) got.TrySetResult(p.SavedPath!); }
                files.ProgressChanged += On;
                var data = System.Text.Encoding.UTF8.GetBytes(name);
                var id = Guid.NewGuid().ToString();
                await s.SendAsync(BridgeMessage.Create(MessageType.FileStart, new FileStartPayload(id, name, data.Length, FileHasher.Sha256Hex(data), 1)));
                await s.SendAsync(BridgeMessage.Create(MessageType.FileChunk, new FileChunkPayload(id, 0, Convert.ToBase64String(data))));
                await s.SendAsync(BridgeMessage.Create(MessageType.FileEnd, new FileEndPayload(id)));
                var path = await got.Task.WithTimeout();
                files.ProgressChanged -= On;
                return path;
            }

            Assert.Equal(first, Path.GetDirectoryName(await Send("a.txt")));
            host.DownloadDirectory = second;
            Assert.Equal(second, Path.GetDirectoryName(await Send("b.txt")));
        }
        finally
        {
            cts.Cancel();
            await host.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ recent transfers

    private static TransferProgress P(string id, TransferDirection dir, TransferStatus status, string? source = null, string? saved = null, string? rel = null) =>
        new() { TransferId = id, FileName = id + ".bin", Direction = dir, Status = status, SourcePath = source, SavedPath = saved, RelativePath = rel, TotalChunks = 4, ChunksDone = 2 };

    [Fact]
    public void Recent_Transfers_Track_Updates_Newest_First_And_Cap()
    {
        var h = new TransferHistory();
        var changed = 0;
        h.Changed += () => changed++;
        for (var i = 0; i < TransferHistory.Capacity + 5; i++) h.Update(P("t" + i, TransferDirection.Sending, TransferStatus.Completed));
        Assert.Equal(TransferHistory.Capacity, h.Entries.Count);
        Assert.Equal("t24", h.Entries[0].TransferId);

        h.Update(P("t10", TransferDirection.Sending, TransferStatus.InProgress));
        Assert.Equal("t10", h.Entries[0].TransferId);
        Assert.Single(h.Entries, e => e.TransferId == "t10");
        // Same id in the other direction is another row.
        h.Update(P("t10", TransferDirection.Receiving, TransferStatus.InProgress));
        Assert.Equal(2, h.Entries.Count(e => e.TransferId == "t10"));

        h.Update(P("p", TransferDirection.Sending, TransferStatus.Interrupted));
        h.ClearFinished();
        Assert.All(h.Entries, e => Assert.True(e.Status is TransferStatus.InProgress or TransferStatus.Interrupted));
        h.Remove("p");
        Assert.DoesNotContain(h.Entries, e => e.TransferId == "p");
        Assert.True(changed > 20);
        Assert.Equal("Sending · 50%", TransferHistory.Describe(h.Entries.First(e => e.Direction == TransferDirection.Sending)).Replace("50 %", "50%"));
    }

    [Fact]
    public void Retry_And_Open_Are_Offered_Only_When_They_Can_Work()
    {
        var dir = Loopback.TempDir();
        var src = Path.Combine(dir, "a.bin"); File.WriteAllText(src, "x");
        var saved = Path.Combine(dir, "b.bin"); File.WriteAllText(saved, "y");
        var h = new TransferHistory();
        h.Update(P("fail", TransferDirection.Sending, TransferStatus.Failed, source: src));
        h.Update(P("refused", TransferDirection.Sending, TransferStatus.Cancelled, source: src));
        h.Update(P("ok", TransferDirection.Sending, TransferStatus.Completed, source: src));
        h.Update(P("gone", TransferDirection.Sending, TransferStatus.Failed, source: Path.Combine(dir, "deleted.bin")));
        h.Update(P("infolder", TransferDirection.Sending, TransferStatus.Failed, source: src, rel: "Album"));
        h.Update(P("got", TransferDirection.Receiving, TransferStatus.Completed, saved: saved));
        h.Update(P("got-moved", TransferDirection.Receiving, TransferStatus.Completed, saved: Path.Combine(dir, "moved.bin")));
        // A final update without the path keeps the one seen earlier.
        h.Update(P("fail", TransferDirection.Sending, TransferStatus.Failed));
        var e = h.Entries.ToDictionary(x => x.TransferId);
        Assert.True(e["fail"].CanRetry);
        Assert.True(e["refused"].CanRetry);
        Assert.False(e["ok"].CanRetry);
        Assert.False(e["gone"].CanRetry);
        Assert.False(e["infolder"].CanRetry);
        Assert.Equal("Album", e["infolder"].Folder);
        Assert.True(e["got"].CanOpen);
        Assert.False(e["got-moved"].CanOpen);
        Assert.False(e["fail"].CanOpen);
    }

    [Fact]
    public async Task Sends_Report_Their_Source_File()
    {
        var src = Path.Combine(Loopback.TempDir(), "s.bin");
        File.WriteAllText(src, "hello");
        var (client, server) = await Loopback.ConnectAsync();
        var pc = DeviceIdentity.Generate();
        var resp = EncryptedChannel.HandshakeAsResponderAsync(server, pc);
        await using var phone = await EncryptedChannel.HandshakeAsInitiatorAsync(client, DeviceIdentity.Generate(), pc.PublicKeyBlob);
        await using var ch = await resp;
        var files = new FileTransferService();
        var seen = new List<string?>();
        files.ProgressChanged += p => { lock (seen) seen.Add(p.SourcePath); };
        var sending = files.SendFileAsync(ch, src);
        while (true) { var m = await phone.ReceiveAsync().WithTimeout(); if (m.Type == MessageType.FileEnd) break; }
        await phone.SendAsync(BridgeMessage.Create(MessageType.FileAck, new FileAckPayload("x", "ok")));
        await sending.WaitAsync(TimeSpan.FromSeconds(10));
        lock (seen) Assert.All(seen, s => Assert.Equal(src, s));
    }
}
