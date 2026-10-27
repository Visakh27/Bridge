using Bridge.Core;
using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

/// <summary>Phase H on the PC: several phones paired and connected at the same time.</summary>
public sealed class MultiPhoneTests : IDisposable
{
    private readonly DeviceIdentity _pc = DeviceIdentity.Generate();
    private readonly InMemoryPairedDeviceStore _store = new();
    private readonly TcpBridgeListener _listener = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly FileTransferService _files = new();
    private readonly string _downloads = Loopback.TempDir();
    private readonly FakeClipboard _clip = new();
    private readonly ClipboardSyncService _sync;
    private readonly BridgeHost _host;
    private readonly PairingService _pairing;

    public MultiPhoneTests()
    {
        _pairing = new PairingService(_pc, _store, "PC");
        _sync = new ClipboardSyncService(_clip) { Enabled = true };
        _host = new BridgeHost(_pc, _store, _pairing, _files, _sync, _downloads);
        _ = _host.RunAsync(_listener, _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _host.DisposeAsync().AsTask().Wait();
        _listener.Dispose();
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; private set; }
        public event Action<string>? LocalClipboardChanged;
        public Task<string?> ReadTextAsync() => Task.FromResult(Text);
        public Task WriteTextAsync(string text) { Text = text; LocalClipboardChanged?.Invoke(text); return Task.CompletedTask; } // like Windows: our own write fires the change event
        public void Copy(string text) { Text = text; LocalClipboardChanged?.Invoke(text); }
    }

    private sealed record Phone(string Name, DeviceIdentity Identity)
    {
        public string Id => Identity.DeviceId;
    }

    private async Task<EncryptedChannel> Connect(DeviceIdentity phone)
    {
        var t = await TcpTransport.ConnectAsync("127.0.0.1", _listener.Port);
        return await EncryptedChannel.HandshakeAsInitiatorAsync(t, phone, _pc.PublicKeyBlob).WithTimeout();
    }

    private async Task<Phone> PairAsync(string name)
    {
        var phone = new Phone(name, DeviceIdentity.Generate());
        var qr = _pairing.CreateQrPayload(_listener.Port, "127.0.0.1");
        await using var p = await Connect(phone.Identity);
        await p.SendAsync(BridgeMessage.Create(MessageType.PairingRequest,
            new PairingRequestPayload(qr.PairingToken, name, Convert.ToBase64String(phone.Identity.PublicKeyBlob))));
        Assert.Equal(MessageType.PairingAccept, (await p.ReceiveAsync().WithTimeout()).Type);
        return phone;
    }

    private static readonly string[] AllFeatures =
        [Features.Files, Features.FilesResume, Features.Clipboard, Features.Notifications, Features.NotificationsActions, Features.Photos, Features.Handoff];

    /// <summary>Connects a paired phone and waits until the PC has its session.</summary>
    private async Task<EncryptedChannel> ConnectAsync(Phone phone, string[]? features = null)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(BridgeSession s) { if (s.Device.DeviceId == phone.Id) started.TrySetResult(); }
        _host.SessionStarted += On;
        try
        {
            var ch = await Connect(phone.Identity);
            await ch.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload(features ?? AllFeatures)));
            var reply = await ch.ReceiveAsync().WithTimeout();
            Assert.Equal(MessageType.CapabilitiesAdvertise, reply.Type);
            await started.Task.WithTimeout();
            for (var i = 0; i < 100 && _host.SessionFor(phone.Id) is not { IsRunning: true }; i++) await Task.Delay(10);
            return ch;
        }
        finally { _host.SessionStarted -= On; }
    }

    private static async Task<BridgeMessage> Next(EncryptedChannel ch, string type)
    {
        while (true) { var m = await ch.ReceiveAsync().WithTimeout(); if (m.Type == type) return m; }
    }

    /// <summary>The next message of <paramref name="type"/> within <paramref name="ms"/>, or null.</summary>
    private static async Task<BridgeMessage?> Maybe(EncryptedChannel ch, string type, int ms = 600)
    {
        using var cts = new CancellationTokenSource(ms);
        try { while (true) { var m = await ch.ReceiveAsync(cts.Token); if (m.Type == type) return m; } }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return null; }
    }

    private async Task WaitGone(string deviceId)
    {
        for (var i = 0; i < 200 && _host.SessionFor(deviceId) is not null; i++) await Task.Delay(20);
        Assert.Null(_host.SessionFor(deviceId));
    }

    // ------------------------------------------------------------------ sessions and the active phone

    [Fact]
    public async Task Two_Phones_Stay_Connected_And_The_Active_One_Follows_The_Users_Pick()
    {
        var pixel = await PairAsync("Pixel");
        var galaxy = await PairAsync("Galaxy");
        var changes = new List<string?>();
        _host.ActiveSessionChanged += s => { lock (changes) changes.Add(s?.Device.DeviceName); };
        // Events arrive on the thread that made the change: wait for each one before the next step.
        async Task Saw(params string?[] expected)
        {
            for (var i = 0; i < 200; i++) { lock (changes) if (changes.Count >= expected.Length) break; await Task.Delay(10); }
            lock (changes) Assert.True(changes.SequenceEqual(expected), string.Join(",", changes.Select(c => c ?? "-")));
        }

        await using var a = await ConnectAsync(pixel);
        Assert.Equal("Pixel", _host.CurrentSession?.Device.DeviceName);
        await Saw("Pixel");
        await using var b = await ConnectAsync(galaxy);
        Assert.Equal(2, _host.Sessions.Count);
        Assert.True(_host.SessionFor(pixel.Id)!.IsRunning);
        Assert.Equal("Galaxy", _host.CurrentSession?.Device.DeviceName); // most recently connected
        await Saw("Pixel", "Galaxy");

        _host.ActiveDeviceId = pixel.Id;
        Assert.Equal("Pixel", _host.CurrentSession?.Device.DeviceName);
        _host.ActiveDeviceId = pixel.Id; // no change, no event
        await Saw("Pixel", "Galaxy", "Pixel");

        // The picked phone leaves: the other one stands in; it comes back: it's active again.
        await a.DisposeAsync();
        await WaitGone(pixel.Id);
        Assert.Equal("Galaxy", _host.CurrentSession?.Device.DeviceName);
        Assert.Equal(pixel.Id, _host.ActiveDeviceId); // still the user's pick
        await Saw("Pixel", "Galaxy", "Pixel", "Galaxy");
        await using var a2 = await ConnectAsync(pixel);
        Assert.Equal("Pixel", _host.CurrentSession?.Device.DeviceName);
        await Saw("Pixel", "Galaxy", "Pixel", "Galaxy", "Pixel");

        _host.ActiveDeviceId = null; // follow the most recent again: that's the Pixel, so nothing changes
        Assert.Equal("Pixel", _host.CurrentSession?.Device.DeviceName);
        await Task.Delay(100);
        await Saw("Pixel", "Galaxy", "Pixel", "Galaxy", "Pixel");
    }

    [Fact]
    public async Task Active_Phone_Events_Stay_In_Order_When_Changes_Happen_Together()
    {
        var phones = new List<Phone>();
        for (var i = 0; i < 4; i++) phones.Add(await PairAsync($"Phone {i}"));
        var seen = new List<BridgeSession?>();
        _host.ActiveSessionChanged += s => { lock (seen) seen.Add(s); };
        var channels = new List<EncryptedChannel>();
        try
        {
            foreach (var p in phones) channels.Add(await ConnectAsync(p, [Features.Files]));
            // Users clicking the picker while phones come and go.
            var picks = Task.Run(() => { for (var i = 0; i < 200; i++) _host.ActiveDeviceId = phones[i % 4].Id; });
            await channels[1].DisposeAsync();
            channels.Add(await ConnectAsync(phones[1], [Features.Files]));
            await picks;
            await Task.Delay(200);
            lock (seen)
            {
                for (var i = 1; i < seen.Count; i++) Assert.False(ReferenceEquals(seen[i], seen[i - 1]), "the same active phone was announced twice in a row");
                Assert.True(ReferenceEquals(seen[^1], _host.CurrentSession), "the last announcement isn't the phone that's active now");
            }
        }
        finally { foreach (var c in channels) await c.DisposeAsync(); }
    }

    [Fact]
    public async Task A_Reconnect_Replaces_Only_That_Phones_Session()
    {
        var pixel = await PairAsync("Pixel");
        var galaxy = await PairAsync("Galaxy");
        await using var a = await ConnectAsync(pixel);
        await using var b = await ConnectAsync(galaxy);
        var oldPixel = _host.SessionFor(pixel.Id)!;
        var galaxySession = _host.SessionFor(galaxy.Id)!;

        await using var a2 = await ConnectAsync(pixel); // half-open old connection, new one arrives
        for (var i = 0; i < 100 && oldPixel.IsRunning; i++) await Task.Delay(10);
        Assert.False(oldPixel.IsRunning);
        Assert.True(galaxySession.IsRunning);
        Assert.Equal(2, _host.Sessions.Count);
        Assert.False(ReferenceEquals(oldPixel, _host.SessionFor(pixel.Id)));
    }

    [Fact]
    public async Task Beyond_The_Limit_A_New_Phone_Is_Refused_But_Connected_Ones_Can_Reconnect()
    {
        var phones = new List<Phone>();
        for (var i = 0; i <= BridgeHost.MaxConnectedPhones; i++) phones.Add(await PairAsync($"Phone {i}"));
        var channels = new List<EncryptedChannel>();
        try
        {
            foreach (var p in phones.Take(BridgeHost.MaxConnectedPhones)) channels.Add(await ConnectAsync(p, [Features.Files]));
            Assert.Equal(BridgeHost.MaxConnectedPhones, _host.Sessions.Count);

            await using (var extra = await Connect(phones[^1].Identity))
            {
                await extra.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload([Features.Files])));
                var reply = await extra.ReceiveAsync().WithTimeout();
                Assert.Equal(MessageType.Error, reply.Type);
                Assert.Equal(ErrorCodes.TooManyDevices, reply.PayloadAs<ErrorPayload>().Code);
            }
            Assert.Null(_host.SessionFor(phones[^1].Id));

            channels.Add(await ConnectAsync(phones[0], [Features.Files])); // a reconnect still works at the limit
            Assert.Equal(BridgeHost.MaxConnectedPhones, _host.Sessions.Count);

            await channels[1].DisposeAsync();
            await WaitGone(phones[1].Id);
            channels.Add(await ConnectAsync(phones[^1], [Features.Files])); // a place freed up
            Assert.NotNull(_host.SessionFor(phones[^1].Id));
        }
        finally { foreach (var c in channels) await c.DisposeAsync(); }
    }

    [Fact]
    public async Task Forgetting_One_Phone_Drops_Only_That_Phone()
    {
        var pixel = await PairAsync("Pixel");
        var galaxy = await PairAsync("Galaxy");
        await using var a = await ConnectAsync(pixel);
        await using var b = await ConnectAsync(galaxy);
        _host.ActiveDeviceId = pixel.Id;
        await _host.RevokeAsync(pixel.Id);
        await WaitGone(pixel.Id);
        Assert.Null(_host.ActiveDeviceId);
        Assert.True(_host.SessionFor(galaxy.Id)!.IsRunning);
        Assert.Equal("Galaxy", _host.CurrentSession?.Device.DeviceName);
        Assert.Single(await _store.ListAsync());
    }

    [Fact]
    public async Task Two_Phones_Sending_Files_With_The_Same_Name_At_Once_Both_Arrive_Intact()
    {
        var pixel = await PairAsync("Pixel");
        var galaxy = await PairAsync("Galaxy");
        await using var a = await ConnectAsync(pixel, [Features.Files]);
        await using var b = await ConnectAsync(galaxy, [Features.Files]);

        async Task<byte[]> Send(EncryptedChannel ch, int seed)
        {
            var data = new byte[3 * FileTransferService.ChunkSize + 123];
            new Random(seed).NextBytes(data);
            var id = Guid.NewGuid().ToString();
            var chunks = (data.Length + FileTransferService.ChunkSize - 1) / FileTransferService.ChunkSize;
            await ch.SendAsync(BridgeMessage.Create(MessageType.FileStart,
                new FileStartPayload(id, "IMG_0001.jpg", data.Length, FileHasher.Sha256Hex(data), chunks)));
            for (var i = 0; i < chunks; i++)
            {
                var part = Convert.ToBase64String(data, i * FileTransferService.ChunkSize, Math.Min(FileTransferService.ChunkSize, data.Length - i * FileTransferService.ChunkSize));
                await ch.SendAsync(BridgeMessage.Create(MessageType.FileChunk, new FileChunkPayload(id, i, part)));
                await Task.Yield();
            }
            await ch.SendAsync(BridgeMessage.Create(MessageType.FileEnd, new FileEndPayload(id)));
            Assert.Equal("ok", (await Next(ch, MessageType.FileAck)).PayloadAs<FileAckPayload>().Status);
            return data;
        }

        var sent = await Task.WhenAll(Send(a, 1), Send(b, 2)); // interleaved on the wire
        var saved = Directory.GetFiles(_downloads, "IMG_0001*.jpg").Select(File.ReadAllBytes).ToList();
        Assert.Equal(2, saved.Count);
        Assert.Contains(saved, f => f.AsSpan().SequenceEqual(sent[0]));
        Assert.Contains(saved, f => f.AsSpan().SequenceEqual(sent[1]));
    }

    // ------------------------------------------------------------------ clipboard

    [Fact]
    public async Task The_PCs_Clipboard_Goes_To_Every_Phone_But_One_Phones_Clipboard_Stays_Off_The_Others()
    {
        var pixel = await PairAsync("Pixel");
        var galaxy = await PairAsync("Galaxy");
        var old = await PairAsync("Old phone");
        await using var a = await ConnectAsync(pixel);
        await using var b = await ConnectAsync(galaxy);
        await using var c = await ConnectAsync(old, [Features.Files]); // no clipboard feature
        Assert.Equal(2, _sync.PeerCount);

        _clip.Copy("from the PC");
        Assert.Equal("from the PC", (await Next(a, MessageType.ClipboardUpdate)).PayloadAs<ClipboardUpdatePayload>().Data);
        Assert.Equal("from the PC", (await Next(b, MessageType.ClipboardUpdate)).PayloadAs<ClipboardUpdatePayload>().Data);
        Assert.Null(await Maybe(c, MessageType.ClipboardUpdate, 300));

        // Pixel's clipboard reaches the PC, never the Galaxy (it may be someone else's phone).
        await a.SendAsync(BridgeMessage.Create(MessageType.ClipboardUpdate, new ClipboardUpdatePayload("text", "pixel secret")));
        for (var i = 0; i < 100 && _clip.Text != "pixel secret"; i++) await Task.Delay(10);
        Assert.Equal("pixel secret", _clip.Text);
        Assert.Null(await Maybe(b, MessageType.ClipboardUpdate));
        Assert.Null(await Maybe(a, MessageType.ClipboardUpdate, 200)); // nor echoed back

        await a.DisposeAsync();
        await WaitGone(pixel.Id);
        Assert.Equal(1, _sync.PeerCount);
        _clip.Copy("after pixel left");
        Assert.Equal("after pixel left", (await Next(b, MessageType.ClipboardUpdate)).PayloadAs<ClipboardUpdatePayload>().Data);
    }

    // ------------------------------------------------------------------ notification actions

    [Fact]
    public async Task A_Toast_Click_Goes_To_The_Phone_The_Notification_Came_From()
    {
        var pixel = await PairAsync("Pixel");
        var galaxy = await PairAsync("Galaxy");
        var router = new NotificationActionRouter(_host, sessionWait: TimeSpan.FromSeconds(3), actionTimeout: TimeSpan.FromSeconds(5));
        await using var a = await ConnectAsync(pixel);
        await using var b = await ConnectAsync(galaxy);
        _host.ActiveDeviceId = galaxy.Id;

        // Same notification key on both phones (same app, same tag): the toast names its phone.
        var command = NotificationToastCommand.Parse(new NotificationToastCommand(NotificationToastCommand.Reply, "0|com.chat|1", "r", DeviceId: pixel.Id)
            .ToArguments().ToDictionary(k => k.Key, v => v.Value));
        Assert.Equal(pixel.Id, command.DeviceId);
        var running = command.RunAsync(router, new Dictionary<string, string> { [NotificationToastCommand.ReplyInputId] = "on my way" });
        var asked = (await Next(a, MessageType.NotificationAction)).PayloadAs<NotificationActionPayload>();
        Assert.Equal("on my way", asked.ReplyText);
        await a.SendAsync(BridgeMessage.Create(MessageType.NotificationActionResult, new NotificationActionResultPayload(asked.RequestId, NotificationActionStatus.Ok)));
        Assert.Equal(NotificationActionStatus.Ok, await running.WithTimeout());
        Assert.Null(await Maybe(b, MessageType.NotificationAction, 300));
        Assert.True(router.TakeRecentAction("0|com.chat|1", pixel.Id));
        Assert.False(router.TakeRecentAction("0|com.chat|1", galaxy.Id));

        // A Galaxy answer to a Pixel request doesn't count.
        var press = router.PressAsync("k", "a1", deviceId: pixel.Id);
        var asked2 = (await Next(a, MessageType.NotificationAction)).PayloadAs<NotificationActionPayload>();
        await b.SendAsync(BridgeMessage.Create(MessageType.NotificationActionResult, new NotificationActionResultPayload(asked2.RequestId, NotificationActionStatus.Gone)));
        await Task.Delay(200);
        Assert.False(press.IsCompleted);
        await a.SendAsync(BridgeMessage.Create(MessageType.NotificationActionResult, new NotificationActionResultPayload(asked2.RequestId, NotificationActionStatus.Ok)));
        Assert.Equal(NotificationActionStatus.Ok, await press.WithTimeout());

        // The phone is away: the click waits for that phone, it never goes to the other one.
        await a.DisposeAsync();
        await WaitGone(pixel.Id);
        var dismiss = router.DismissAsync("k", deviceId: pixel.Id);
        Assert.Null(await Maybe(b, MessageType.NotificationDismiss, 500));
        await using var a2 = await ConnectAsync(pixel);
        Assert.Equal("k", (await Next(a2, MessageType.NotificationDismiss)).PayloadAs<NotificationDismissPayload>().Key);
        Assert.True(await dismiss.WithTimeout());

        // Toasts from before several phones (no phone in them) go to the active phone.
        var legacy = NotificationToastCommand.Parse(new Dictionary<string, string> { ["bridge"] = "dismiss", ["key"] = "aw" });
        Assert.Null(legacy.DeviceId);
        _host.ActiveDeviceId = galaxy.Id;
        var t = legacy.RunAsync(router, null);
        await Next(b, MessageType.NotificationDismiss);
        await t.WithTimeout();
    }

    // ------------------------------------------------------------------ photos

    [Fact]
    public async Task Photos_Come_From_The_Active_Phone_Only()
    {
        var pixel = await PairAsync("Pixel");
        var galaxy = await PairAsync("Galaxy");
        var browser = new PhotoBrowser(_host, _files, timeout: TimeSpan.FromSeconds(2));
        var changed = 0;
        browser.Changed += () => Interlocked.Increment(ref changed);
        await using var a = await ConnectAsync(pixel);
        await using var b = await ConnectAsync(galaxy);
        _host.ActiveDeviceId = pixel.Id;
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 9, 9];

        async Task<PhotoThumb> Thumb(EncryptedChannel asked, EncryptedChannel? impostor, byte tag)
        {
            var t = browser.ThumbnailAsync("7");
            var req = (await Next(asked, MessageType.PhotosThumb)).PayloadAs<PhotosThumbPayload>();
            byte[] img = [0xFF, 0xD8, 0xFF, tag];
            if (impostor is not null) // the other phone answers first: it must not count
            {
                await impostor.SendAsync(BridgeMessage.Create(MessageType.PhotosThumbnail, new PhotosThumbnailPayload(req.RequestId, "7", PhotoStatus.Ok) { Mime = "image/jpeg", Data = Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0x66]) }));
                await Task.Delay(300);
                Assert.False(t.IsCompleted, "an answer from a phone that wasn't asked was accepted");
            }
            await asked.SendAsync(BridgeMessage.Create(MessageType.PhotosThumbnail, new PhotosThumbnailPayload(req.RequestId, "7", PhotoStatus.Ok) { Mime = "image/jpeg", Data = Convert.ToBase64String(img) }));
            return await t.WithTimeout();
        }

        Assert.Equal(0x11, (await Thumb(a, b, 0x11)).Jpeg![3]);
        Assert.Equal(0x11, (await browser.ThumbnailAsync("7")).Jpeg![3]); // cached for the Pixel
        _host.ActiveDeviceId = galaxy.Id;
        Assert.Equal(0x22, (await Thumb(b, null, 0x22)).Jpeg![3]); // photo 7 on the Galaxy is a different photo
        _host.ActiveDeviceId = pixel.Id;
        Assert.Equal(0x11, (await browser.ThumbnailAsync("7")).Jpeg![3]);

        await b.SendAsync(BridgeMessage.CreateEmpty(MessageType.PhotosChanged)); // not the phone being browsed
        await a.SendAsync(BridgeMessage.CreateEmpty(MessageType.PhotosChanged));
        for (var i = 0; i < 100 && changed == 0; i++) await Task.Delay(10);
        await Task.Delay(200);
        Assert.Equal(1, changed);
    }

    // ------------------------------------------------------------------ handoff

    [Fact]
    public async Task Handoffs_Have_A_Limit_Per_Phone_And_Go_To_The_Chosen_Phone()
    {
        var pixel = await PairAsync("Pixel");
        var galaxy = await PairAsync("Galaxy");
        var handoff = new HandoffService(_host);
        var from = new List<string>();
        handoff.TextReceived += (_, phone) => { lock (from) from.Add(phone); };
        await using var a = await ConnectAsync(pixel);
        await using var b = await ConnectAsync(galaxy);

        async Task<string> Hand(EncryptedChannel ch, string id)
        {
            await ch.SendAsync(BridgeMessage.Create(MessageType.HandoffOpen, new HandoffPayload(id, HandoffKind.Text) { Text = "t" }));
            return (await Next(ch, MessageType.HandoffAck)).PayloadAs<HandoffAckPayload>().Status;
        }
        for (var i = 0; i < HandoffService.MaxPerMinute; i++) Assert.Equal(HandoffStatus.Copied, await Hand(a, $"x{i}"));
        Assert.Equal(HandoffStatus.Busy, await Hand(a, "over"));
        Assert.Equal(HandoffStatus.Copied, await Hand(b, "x0")); // its own allowance, and the same id from another phone is a different handoff
        lock (from) Assert.Equal(1, from.Count(p => p == "Galaxy"));

        _host.ActiveDeviceId = galaxy.Id;
        var toPixel = handoff.SendTextAsync("for the pixel", deviceId: pixel.Id);
        var got = (await Next(a, MessageType.HandoffOpen)).PayloadAs<HandoffPayload>();
        Assert.Equal("for the pixel", got.Text);
        await b.SendAsync(BridgeMessage.Create(MessageType.HandoffAck, new HandoffAckPayload(got.Id, HandoffStatus.Disabled))); // not from the Pixel: ignored
        await Task.Delay(300);
        Assert.False(toPixel.IsCompleted, "an answer from a phone that wasn't asked was accepted");
        await a.SendAsync(BridgeMessage.Create(MessageType.HandoffAck, new HandoffAckPayload(got.Id, HandoffStatus.Copied)));
        Assert.Equal(HandoffStatus.Copied, await toPixel.WithTimeout());

        var toActive = handoff.SendTextAsync("for the active phone");
        var got2 = (await Next(b, MessageType.HandoffOpen)).PayloadAs<HandoffPayload>();
        await b.SendAsync(BridgeMessage.Create(MessageType.HandoffAck, new HandoffAckPayload(got2.Id, HandoffStatus.Copied)));
        Assert.Equal(HandoffStatus.Copied, await toActive.WithTimeout());
        Assert.Equal(HandoffStatus.Unavailable, await handoff.SendTextAsync("x", deviceId: "not-connected"));
    }

    // ------------------------------------------------------------------ File Explorer

    [Fact]
    public async Task A_Waiting_Explorer_Send_Goes_To_The_Picked_Phone_Not_The_First_To_Connect()
    {
        var pixel = await PairAsync("Pixel");
        var galaxy = await PairAsync("Galaxy");
        var queue = new ExplorerSendQueue(_host, startDelay: TimeSpan.FromMilliseconds(50));
        var finished = new TaskCompletionSource<SendSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Finished += s => finished.TrySetResult(s);
        var file = Path.Combine(Loopback.TempDir(), "for-pixel.txt");
        await File.WriteAllTextAsync(file, "hello");

        _host.ActiveDeviceId = pixel.Id; // picked, but nobody is connected
        Assert.Null(await queue.SubmitAsync(new SendRequest([file], [], [])));
        await using var b = await ConnectAsync(galaxy);
        Assert.Null(await Maybe(b, MessageType.FileStart, 800));
        Assert.Equal(1, queue.PendingItems);

        await using var a = await ConnectAsync(pixel);
        var start = (await Next(a, MessageType.FileStart)).PayloadAs<FileStartPayload>();
        Assert.Equal("for-pixel.txt", start.FileName);
        if (start.Resumable == true) await a.SendAsync(BridgeMessage.Create(MessageType.FileResume, new FileResumePayload(start.TransferId, 0)));
        await Next(a, MessageType.FileEnd);
        await a.SendAsync(BridgeMessage.Create(MessageType.FileAck, new FileAckPayload(start.TransferId, "ok")));
        Assert.Equal("Pixel", (await finished.Task.WithTimeout()).DeviceName);
        Assert.Equal(0, queue.PendingItems);
    }

    // ------------------------------------------------------------------ diagnostics

    [Fact]
    public async Task The_Log_Counts_Phones_Without_Naming_Them()
    {
        var log = new DiagnosticsLog(null);
        _ = new BridgeDiagnostics(log, _host, _files);
        var pixel = await PairAsync("Pixel ZEBRA");
        var galaxy = await PairAsync("Galaxy ZEBRA");
        await using var a = await ConnectAsync(pixel, [Features.Files]);
        await using (var b = await ConnectAsync(galaxy, [Features.Files]))
        {
            // The Galaxy becomes active as it connects; picking the Pixel before that is announced would
            // (rightly) leave nothing to announce, since only the active phone as it is now is reported.
            for (var i = 0; i < 200 && !log.Lines().Any(l => l.Contains("phone.active") && l.Contains("phones=2")); i++) await Task.Delay(10);
            _host.ActiveDeviceId = pixel.Id;
        }
        await WaitGone(galaxy.Id);
        // The log's own "ended" handler runs after the host's: give it a moment.
        for (var i = 0; i < 200 && !log.Lines().Any(l => l.Contains("session.ended")); i++) await Task.Delay(10);
        var lines = log.Lines();
        var dump = string.Join(" | ", lines.Select(l => l[(l.IndexOf(' ') + 1)..]));
        Assert.True(lines.Any(l => l.Contains("session.started") && l.Contains("phones=2")), "started: " + dump);
        Assert.True(lines.Any(l => l.Contains("phone.active") && l.Contains("phones=2")), "active: " + dump);
        Assert.True(lines.Any(l => l.Contains("session.ended") && l.Contains("phones=1")), "ended: " + dump);
        foreach (var l in lines) Assert.False(l.Contains("ZEBRA") || l.Contains(pixel.Id) || l.Contains(galaxy.Id), l);
    }
}
