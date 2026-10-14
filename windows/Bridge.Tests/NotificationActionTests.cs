using Bridge.Core;
using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

/// <summary>Phase C on the PC: replying to, pressing, dismissing and muting phone notifications.</summary>
public sealed class NotificationActionTests : IDisposable
{
    private readonly DeviceIdentity _pc = DeviceIdentity.Generate();
    private readonly InMemoryPairedDeviceStore _store = new();
    private readonly TcpBridgeListener _listener = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly BridgeHost _host;
    private readonly PairingService _pairing;

    public NotificationActionTests()
    {
        _pairing = new PairingService(_pc, _store, "Test PC");
        _host = new BridgeHost(_pc, _store, _pairing, new FileTransferService(), new ClipboardSyncService(new NoClipboard()), Loopback.TempDir());
        _ = _host.RunAsync(_listener, _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _host.DisposeAsync().AsTask().Wait();
        _listener.Dispose();
    }

    private sealed class NoClipboard : IClipboardAccess
    {
        public event Action<string>? LocalClipboardChanged { add { } remove { } }
        public Task<string?> ReadTextAsync() => Task.FromResult<string?>(null);
        public Task WriteTextAsync(string text) => Task.CompletedTask;
    }

    private async Task<EncryptedChannel> ConnectAsync(DeviceIdentity phone)
    {
        var transport = await TcpTransport.ConnectAsync("127.0.0.1", _listener.Port);
        return await EncryptedChannel.HandshakeAsInitiatorAsync(transport, phone, _pc.PublicKeyBlob).WithTimeout();
    }

    private async Task<DeviceIdentity> PairAsync()
    {
        var phone = DeviceIdentity.Generate();
        var qr = _pairing.CreateQrPayload(_listener.Port, "127.0.0.1");
        await using var ch = await ConnectAsync(phone);
        await ch.SendAsync(BridgeMessage.Create(MessageType.PairingRequest,
            new PairingRequestPayload(qr.PairingToken, "Pixel", Convert.ToBase64String(phone.PublicKeyBlob))));
        Assert.Equal(MessageType.PairingAccept, (await ch.ReceiveAsync().WithTimeout()).Type);
        return phone;
    }

    /// <summary>A connected phone; returns the PC's advertised features too.</summary>
    private async Task<(EncryptedChannel Channel, BridgeSession Session, string[] PcFeatures)> ConnectPhoneAsync(DeviceIdentity phone, bool actions = true)
    {
        var started = new TaskCompletionSource<BridgeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStarted(BridgeSession s) => started.TrySetResult(s);
        _host.SessionStarted += OnStarted;
        var ch = await ConnectAsync(phone);
        string[] features = actions ? [Features.Files, Features.Notifications, Features.NotificationsActions] : [Features.Files, Features.Notifications];
        await ch.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload(features)));
        var reply = await ch.ReceiveAsync().WithTimeout();
        var session = await started.Task.WithTimeout();
        _host.SessionStarted -= OnStarted;
        return (ch, session, reply.PayloadAs<CapabilitiesPayload>().Features);
    }

    /// <summary>Reads until a message of <paramref name="type"/> (skipping pings).</summary>
    private static async Task<BridgeMessage> NextAsync(EncryptedChannel ch, string type)
    {
        while (true)
        {
            var m = await ch.ReceiveAsync().WithTimeout();
            if (m.Type == type) return m;
        }
    }

    private static NotificationPostPayload Note(string key = "k1") => new("Chat", "Ana", "Lunch?", key)
    {
        AppId = "com.chat",
        CanDismiss = true,
        Actions = [new("0-aaaaaaaa", "Reply") { Reply = true }, new("1-bbbbbbbb", "Mark as read")],
    };

    [Fact]
    public async Task Pc_Advertises_Actions_And_Shows_Buttons_Only_For_Phones_That_Support_Them()
    {
        var phone = await PairAsync();
        var (ch, session, pcFeatures) = await ConnectPhoneAsync(phone);
        await using (ch)
        {
            Assert.True(pcFeatures.Contains(Features.NotificationsActions));
            var seen = new TaskCompletionSource<NotificationPostPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.NotificationReceived += n => seen.TrySetResult(n);
            await ch.SendAsync(BridgeMessage.Create(MessageType.NotificationPost, Note()));
            var shown = await seen.Task.WithTimeout();
            Assert.Equal(2, shown.Actions!.Length);
            Assert.True(shown.CanDismiss == true);
            Assert.Equal("com.chat", shown.AppId);
        }

        var phone2 = await PairAsync();
        var (ch2, oldPhone, _) = await ConnectPhoneAsync(phone2, actions: false);
        await using (ch2)
        {
            var seen = new TaskCompletionSource<NotificationPostPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            oldPhone.NotificationReceived += n => seen.TrySetResult(n);
            await ch2.SendAsync(BridgeMessage.Create(MessageType.NotificationPost, Note()));
            var shown = await seen.Task.WithTimeout();
            Assert.Null(shown.Actions); // nothing to press: that phone couldn't carry it out
            Assert.Null(shown.CanDismiss);
            Assert.Equal(NotificationActionStatus.Unavailable, await oldPhone.InvokeNotificationActionAsync("k1", "1-bbbbbbbb", null));
            Assert.False(await oldPhone.DismissOnPhoneAsync("k1"));
            Assert.False(await oldPhone.MuteAppOnPhoneAsync("com.chat"));
        }
    }

    [Fact]
    public async Task Reply_Round_Trip_Carries_The_Text_And_The_Phones_Answer()
    {
        var phone = await PairAsync();
        var (ch, session, _) = await ConnectPhoneAsync(phone);
        await using (ch)
        {
            var reply = session.InvokeNotificationActionAsync("k1", "0-aaaaaaaa", "On my way");
            var request = (await NextAsync(ch, MessageType.NotificationAction)).PayloadAs<NotificationActionPayload>();
            Assert.Equal(("k1", "0-aaaaaaaa", "On my way"), (request.Key, request.ActionId, request.ReplyText));
            await ch.SendAsync(BridgeMessage.Create(MessageType.NotificationActionResult,
                new NotificationActionResultPayload(request.RequestId, NotificationActionStatus.Gone)));
            Assert.Equal(NotificationActionStatus.Gone, await reply.WithTimeout());

            // A plain button carries no text on the wire.
            var press = session.InvokeNotificationActionAsync("k1", "1-bbbbbbbb", null);
            var raw = await NextAsync(ch, MessageType.NotificationAction);
            Assert.DoesNotContain("replyText", raw.ToJson());
            await ch.SendAsync(BridgeMessage.Create(MessageType.NotificationActionResult,
                new NotificationActionResultPayload(raw.PayloadAs<NotificationActionPayload>().RequestId, NotificationActionStatus.Ok)));
            Assert.Equal(NotificationActionStatus.Ok, await press.WithTimeout());
        }
    }

    [Fact]
    public async Task Invalid_Replies_Are_Not_Sent()
    {
        var phone = await PairAsync();
        var (ch, session, _) = await ConnectPhoneAsync(phone);
        await using (ch)
        {
            Assert.Equal(NotificationActionStatus.Invalid, await session.InvokeNotificationActionAsync("k1", "0-a", "  "));
            Assert.Equal(NotificationActionStatus.Invalid, await session.InvokeNotificationActionAsync("k1", "0-a", new string('x', PhoneNotificationText.MaxReply + 1)));
            var router = new NotificationActionRouter(_host);
            Assert.Equal(NotificationActionStatus.Invalid, await router.ReplyAsync("k1", "0-a", ""));
            Assert.Equal(NotificationActionStatus.Invalid, await router.ReplyAsync("k1", "0-a", null));
            // Nothing reached the phone: the next thing it sees is our ping answer.
            await ch.SendAsync(BridgeMessage.CreateEmpty(MessageType.PresencePing));
            Assert.Equal(MessageType.PresencePong, (await ch.ReceiveAsync().WithTimeout()).Type);
        }
    }

    [Fact]
    public async Task No_Answer_Or_A_Dropped_Phone_Ends_The_Wait()
    {
        var phone = await PairAsync();
        var (ch, session, _) = await ConnectPhoneAsync(phone);
        Assert.Equal(NotificationActionStatus.Unavailable,
            await session.InvokeNotificationActionAsync("k1", "1-b", null, TimeSpan.FromMilliseconds(300)).WithTimeout());

        var waiting = session.InvokeNotificationActionAsync("k1", "1-b", null, TimeSpan.FromSeconds(30));
        await NextAsync(ch, MessageType.NotificationAction);
        var started = DateTime.UtcNow;
        await ch.DisposeAsync(); // phone gone
        Assert.Equal(NotificationActionStatus.Unavailable, await waiting.WithTimeout());
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "should not wait for the timeout");
        Assert.Equal(NotificationActionStatus.Unavailable, await session.InvokeNotificationActionAsync("k1", "1-b", null));
    }

    [Fact]
    public async Task Stray_Or_Malformed_Results_Are_Ignored()
    {
        var phone = await PairAsync();
        var (ch, session, _) = await ConnectPhoneAsync(phone);
        await using (ch)
        {
            var real = session.InvokeNotificationActionAsync("k1", "1-b", null);
            var request = (await NextAsync(ch, MessageType.NotificationAction)).PayloadAs<NotificationActionPayload>();
            await ch.SendAsync(BridgeMessage.Create(MessageType.NotificationActionResult, new NotificationActionResultPayload("someone-else", "ok")));
            await ch.SendAsync(BridgeMessage.CreateEmpty(MessageType.NotificationActionResult)); // no fields at all
            await Task.Delay(200);
            Assert.False(real.IsCompleted);
            await ch.SendAsync(BridgeMessage.Create(MessageType.NotificationActionResult, new NotificationActionResultPayload(request.RequestId, "failed")));
            Assert.Equal(NotificationActionStatus.Failed, await real.WithTimeout());
            Assert.True(session.IsRunning);
        }
    }

    [Fact]
    public async Task Dismiss_And_Mute_Reach_The_Phone()
    {
        var phone = await PairAsync();
        var (ch, session, _) = await ConnectPhoneAsync(phone);
        await using (ch)
        {
            var router = new NotificationActionRouter(_host);
            Assert.True(await router.DismissAsync("k1"));
            Assert.Equal("k1", (await NextAsync(ch, MessageType.NotificationDismiss)).PayloadAs<NotificationDismissPayload>().Key);
            Assert.True(await router.MuteAppAsync("com.chat"));
            Assert.Equal("com.chat", (await NextAsync(ch, MessageType.NotificationMute)).PayloadAs<NotificationMutePayload>().AppId);

            // The phone's own dismissals still reach the UI.
            var gone = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.NotificationDismissed += k => gone.TrySetResult(k);
            await ch.SendAsync(BridgeMessage.Create(MessageType.NotificationDismiss, new NotificationDismissPayload("k1")));
            Assert.Equal("k1", await gone.Task.WithTimeout());
        }
    }

    [Fact]
    public async Task Router_Waits_For_The_Phone_To_Reconnect()
    {
        var phone = await PairAsync();
        var router = new NotificationActionRouter(_host, sessionWait: TimeSpan.FromSeconds(10));
        var press = router.PressAsync("k1", "1-b");
        await Task.Delay(500);
        Assert.False(press.IsCompleted);
        var (ch, _, _) = await ConnectPhoneAsync(phone);
        await using (ch)
        {
            var request = (await NextAsync(ch, MessageType.NotificationAction)).PayloadAs<NotificationActionPayload>();
            await ch.SendAsync(BridgeMessage.Create(MessageType.NotificationActionResult, new NotificationActionResultPayload(request.RequestId, "ok")));
            Assert.Equal(NotificationActionStatus.Ok, await press.WithTimeout());
        }
    }

    [Fact]
    public async Task Router_Gives_Up_When_No_Capable_Phone_Connects()
    {
        var router = new NotificationActionRouter(_host, sessionWait: TimeSpan.FromMilliseconds(400));
        Assert.Equal(NotificationActionStatus.Unavailable, await router.PressAsync("k1", "1-b").WithTimeout());
        Assert.False(await router.DismissAsync("k1").WithTimeout());

        // A phone without the feature doesn't count.
        var phone = await PairAsync();
        var (ch, _, _) = await ConnectPhoneAsync(phone, actions: false);
        await using (ch)
            Assert.Equal(NotificationActionStatus.Unavailable, await router.PressAsync("k1", "1-b").WithTimeout());
    }

    [Fact]
    public void Untrusted_Notification_Text_Is_Cleaned()
    {
        var spoof = "Invoice‮fdp.exe";
        var cleaned = PhoneNotificationText.Clean(new NotificationPostPayload("Mail\u0007", spoof, "line1\nline2\r⁦x⁩", "k")
        {
            AppId = "com.mail; rm -rf",
            Actions =
            [
                new("0-a", "One"), new("0-a", "Duplicate id"), new("", "No id"), new("2-c", "  "),
                new("3-d", "Two"), new("4-e", "Three"), new("5-f", "Four"), new(new string('i', 65), "Long id"),
            ],
        })!;
        Assert.Equal("Mail", cleaned.AppName);
        Assert.Equal("Invoicefdp.exe", cleaned.Title);
        Assert.Equal("line1\nline2 x", cleaned.Text);
        Assert.Null(cleaned.AppId);
        Assert.Equal(["One", "Two", "Three"], cleaned.Actions!.Select(a => a.Title));

        var emoji = string.Concat(Enumerable.Repeat("😀", 150));
        var clipped = PhoneNotificationText.Clean(new NotificationPostPayload("", emoji, "", "k"))!;
        Assert.Equal("Phone", clipped.AppName);
        Assert.True(clipped.Title.Length <= PhoneNotificationText.MaxTitle + 1);
        Assert.False(char.IsHighSurrogate(clipped.Title[^2]));

        Assert.Null(PhoneNotificationText.Clean(new NotificationPostPayload("A", " ‮ ", "t", "k")));
        Assert.Null(PhoneNotificationText.Clean(new NotificationPostPayload("A", "T", "t", "")));
        Assert.Null(PhoneNotificationText.Clean(new NotificationPostPayload("A", "T", "t", new string('k', 2000))));
        Assert.Null(PhoneNotificationText.Clean(BridgeMessage.FromJson(
            BridgeMessage.CreateEmpty(MessageType.NotificationPost).ToJson()).PayloadAs<NotificationPostPayload>()));
    }

    [Fact]
    public async Task Malformed_Posts_Do_Not_Reach_The_Ui_Or_Drop_The_Session()
    {
        var phone = await PairAsync();
        var (ch, session, _) = await ConnectPhoneAsync(phone);
        await using (ch)
        {
            var shown = new List<string>();
            session.NotificationReceived += n => { lock (shown) shown.Add(n.Title); };
            await ch.SendAsync(BridgeMessage.CreateEmpty(MessageType.NotificationPost));
            await ch.SendAsync(BridgeMessage.Create(MessageType.NotificationPost, new { appName = "A", title = "T", text = "x", key = "k", actions = "not-a-list" }));
            await ch.SendAsync(BridgeMessage.Create(MessageType.NotificationPost, Note("ok")));
            await ch.SendAsync(BridgeMessage.CreateEmpty(MessageType.PresencePing));
            Assert.Equal(MessageType.PresencePong, (await NextAsync(ch, MessageType.PresencePong)).Type);
            lock (shown) Assert.Equal(["Ana"], shown);
            Assert.True(session.IsRunning);
        }
    }

    [Fact]
    public async Task Toast_Commands_Round_Trip_And_Run()
    {
        foreach (var c in new[]
        {
            new NotificationToastCommand(NotificationToastCommand.Reply, "0|com.chat|7|null|10123", "0-aaaaaaaa"),
            new NotificationToastCommand(NotificationToastCommand.Press, "k", "1-b"),
            new NotificationToastCommand(NotificationToastCommand.Dismiss, "k"),
            new NotificationToastCommand(NotificationToastCommand.Mute, AppId: "com.chat"),
        })
            Assert.Equal(c, NotificationToastCommand.Parse(new Dictionary<string, string>(c.ToArguments())));

        Assert.Equal(NotificationToastCommand.Open, NotificationToastCommand.Parse(null).Kind);
        Assert.Equal(NotificationToastCommand.Open, NotificationToastCommand.Parse(new Dictionary<string, string>()).Kind);
        Assert.Equal(NotificationToastCommand.Open, NotificationToastCommand.Parse(new Dictionary<string, string>(
            new NotificationToastCommand(NotificationToastCommand.Reply, "k").ToArguments())).Kind); // reply without a button id
        Assert.Equal(NotificationToastCommand.Open, NotificationToastCommand.Parse(new Dictionary<string, string> { ["bridge"] = "format-disk" }).Kind);

        var phone = await PairAsync();
        var (ch, _, _) = await ConnectPhoneAsync(phone);
        await using (ch)
        {
            var router = new NotificationActionRouter(_host);
            var command = NotificationToastCommand.Parse(new Dictionary<string, string>(
                new NotificationToastCommand(NotificationToastCommand.Reply, "k", "0-a").ToArguments()));
            var run = command.RunAsync(router, new Dictionary<string, string> { [NotificationToastCommand.ReplyInputId] = "  typed  " });
            var request = (await NextAsync(ch, MessageType.NotificationAction)).PayloadAs<NotificationActionPayload>();
            Assert.Equal("typed", request.ReplyText);
            await ch.SendAsync(BridgeMessage.Create(MessageType.NotificationActionResult, new NotificationActionResultPayload(request.RequestId, "refused")));
            var status = await run.WithTimeout();
            Assert.Equal(NotificationActionStatus.Refused, status);
            Assert.Contains("Chat", command.FailureMessage(status, "Chat"));
            Assert.Null(command.FailureMessage(NotificationActionStatus.Ok));
            Assert.Null(await new NotificationToastCommand(NotificationToastCommand.Open).RunAsync(router, null));
            Assert.Equal(NotificationActionStatus.Invalid,
                await command.RunAsync(router, new Dictionary<string, string>())); // the box was left empty
        }
    }

    [Fact]
    public void Toast_Arguments_Survive_Any_Characters_In_Keys()
    {
        foreach (var key in new[] { "0|com.x|1|a=b;c=d|1", "%41;;==", "ключ|😀", new string('k', 900) })
        {
            var c = new NotificationToastCommand(NotificationToastCommand.Press, key, "1-b");
            var args = c.ToArguments();
            Assert.True(args.Values.All(v => v.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')), "argument values are plain");
            Assert.Equal(c, NotificationToastCommand.Parse(new Dictionary<string, string>(args)));
        }
        // Not base64, or not UTF-8: just open Bridge.
        Assert.Equal(NotificationToastCommand.Open, NotificationToastCommand.Parse(new Dictionary<string, string> { ["bridge"] = "dismiss", ["key"] = "!!!" }).Kind);
        Assert.Equal(NotificationToastCommand.Open, NotificationToastCommand.Parse(new Dictionary<string, string> { ["bridge"] = "dismiss", ["key"] = "_-8" }).Kind);
    }

    [Fact]
    public async Task The_Apps_Echo_After_A_Reply_Is_Recognised_Even_Before_The_Answer()
    {
        var phone = await PairAsync();
        var (ch, _, _) = await ConnectPhoneAsync(phone);
        await using (ch)
        {
            var router = new NotificationActionRouter(_host);
            var reply = router.ReplyAsync("k1", "0-a", "hi");
            var request = (await NextAsync(ch, MessageType.NotificationAction)).PayloadAs<NotificationActionPayload>();
            Assert.True(router.TakeRecentAction("k1"), "the update can arrive before the phone's answer");
            Assert.False(router.TakeRecentAction("k1"), "only the first update is quiet");
            await ch.SendAsync(BridgeMessage.Create(MessageType.NotificationActionResult, new NotificationActionResultPayload(request.RequestId, "ok")));
            Assert.Equal("ok", await reply.WithTimeout());

            var failed = router.PressAsync("k2", "1-b");
            var r2 = (await NextAsync(ch, MessageType.NotificationAction)).PayloadAs<NotificationActionPayload>();
            await ch.SendAsync(BridgeMessage.Create(MessageType.NotificationActionResult, new NotificationActionResultPayload(r2.RequestId, "gone")));
            Assert.Equal("gone", await failed.WithTimeout());
            Assert.False(router.TakeRecentAction("k2"), "a failed press leaves the next update loud");
            Assert.False(router.TakeRecentAction("never"));
        }
    }

    [Fact]
    public async Task Random_Notification_Junk_From_The_Phone_Never_Drops_The_Session()
    {
        var phone = await PairAsync();
        var (ch, session, _) = await ConnectPhoneAsync(phone);
        await using (ch)
        {
            var rng = new Random(1234);
            string[] types = [MessageType.NotificationPost, MessageType.NotificationDismiss, MessageType.NotificationActionResult];
            object?[] values = [null, 1, -1, true, "", "x", new string('y', 5000), new object[] { 1, "a" }, new { id = 1 }, "\u202Eevil"];
            string[] fields = ["appName", "title", "text", "key", "appId", "actions", "canDismiss", "requestId", "status"];
            for (var i = 0; i < 300; i++)
            {
                var payload = new Dictionary<string, object?>();
                foreach (var f in fields) if (rng.Next(2) == 0) payload[f] = values[rng.Next(values.Length)];
                if (rng.Next(3) == 0) payload["actions"] = Enumerable.Range(0, rng.Next(0, 9))
                    .Select(_ => new Dictionary<string, object?> { ["id"] = values[rng.Next(values.Length)], ["title"] = values[rng.Next(values.Length)], ["reply"] = values[rng.Next(values.Length)] }).ToArray();
                await ch.SendAsync(BridgeMessage.Create(types[rng.Next(types.Length)], payload));
            }
            await ch.SendAsync(BridgeMessage.CreateEmpty(MessageType.PresencePing));
            await NextAsync(ch, MessageType.PresencePong);
            Assert.True(session.IsRunning);
        }
    }
}
