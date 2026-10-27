using Bridge.Core;
using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

/// <summary>Phase G on the PC: links and text handed between phone and PC.</summary>
public sealed class HandoffTests : IDisposable
{
    private readonly DeviceIdentity _pc = DeviceIdentity.Generate();
    private readonly InMemoryPairedDeviceStore _store = new();
    private readonly TcpBridgeListener _listener = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly FileTransferService _files = new();
    private readonly string _downloads = Loopback.TempDir();
    private readonly BridgeHost _host;
    private readonly PairingService _pairing;

    public HandoffTests()
    {
        _pairing = new PairingService(_pc, _store, "PC");
        _host = new BridgeHost(_pc, _store, _pairing, _files, new ClipboardSyncService(new NoClipboard()), _downloads);
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

    private async Task<EncryptedChannel> Connect(DeviceIdentity phone)
    {
        var t = await TcpTransport.ConnectAsync("127.0.0.1", _listener.Port);
        return await EncryptedChannel.HandshakeAsInitiatorAsync(t, phone, _pc.PublicKeyBlob).WithTimeout();
    }

    private async Task<(EncryptedChannel Ch, string[] PcFeatures)> PhoneAsync(bool handoff = true)
    {
        var phone = DeviceIdentity.Generate();
        var qr = _pairing.CreateQrPayload(_listener.Port, "127.0.0.1");
        await using (var p = await Connect(phone))
        {
            await p.SendAsync(BridgeMessage.Create(MessageType.PairingRequest,
                new PairingRequestPayload(qr.PairingToken, "Pixel", Convert.ToBase64String(phone.PublicKeyBlob))));
            await p.ReceiveAsync().WithTimeout();
        }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(BridgeSession _) => started.TrySetResult();
        _host.SessionStarted += On;
        var ch = await Connect(phone);
        string[] f = handoff ? [Features.Files, Features.Handoff] : [Features.Files];
        await ch.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload(f)));
        var reply = await ch.ReceiveAsync().WithTimeout();
        await started.Task.WithTimeout();
        _host.SessionStarted -= On;
        return (ch, reply.PayloadAs<CapabilitiesPayload>().Features);
    }

    private static async Task<BridgeMessage> Next(EncryptedChannel ch, string type)
    {
        while (true) { var m = await ch.ReceiveAsync().WithTimeout(); if (m.Type == type) return m; }
    }

    private static async Task<HandoffAckPayload> Hand(EncryptedChannel ch, HandoffPayload p)
    {
        await ch.SendAsync(BridgeMessage.Create(MessageType.HandoffOpen, p));
        var ack = (await Next(ch, MessageType.HandoffAck)).PayloadAs<HandoffAckPayload>();
        Assert.Equal(p.Id, ack.Id);
        return ack;
    }

    private static HandoffPayload Url(string id, string url, string? title = null) => new(id, HandoffKind.Url) { Url = url, Title = title };
    private static HandoffPayload Text(string id, string text) => new(id, HandoffKind.Text) { Text = text };

    // ------------------------------------------------------------------ rules

    [Theory]
    [InlineData("https://example.com/a?b=c#d", "example.com")]
    [InlineData("http://EXAMPLE.com:8080/", "example.com")]
    [InlineData("https://bücher.de/x", "xn--bcher-kva.de")]
    [InlineData("HTTPS://BÜCHER.de:8443/ä?q=ö#f", "xn--bcher-kva.de")]
    [InlineData("https://192.168.1.10/", "192.168.1.10")]
    [InlineData("https://[2001:db8::1]:443/", "[2001:db8::1]")]
    public void Accepts_Web_Links(string url, string host)
    {
        var link = HandoffRules.CheckUrl(url);
        Assert.NotNull(link);
        Assert.Equal(host, link!.Host);
        Assert.StartsWith("http", link.Url);
        // What's sent and opened carries the host that's shown, in ASCII.
        Assert.True(link.Url.All(c => c < 128), link.Url);
        Assert.Equal(host, new Uri(link.Url).Host.Length > 0 && host.StartsWith('[') ? "[" + new Uri(link.Url).Host.Trim('[', ']') + "]" : new Uri(link.Url).Host);
        Assert.Equal(link, HandoffRules.CheckUrl(link.Url)); // stable
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("intent://scan/#Intent;scheme=zxing;end")]
    [InlineData("ftp://example.com/")]
    [InlineData("https://user:pw@example.com/")]
    [InlineData("https://example.com@evil.com/")]
    [InlineData("https://exa mple.com/")]
    [InlineData("https://example.com/\nx")]
    [InlineData("https:///nohost")]
    [InlineData("example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_Anything_But_Plain_Web_Links(string? url) => Assert.Null(HandoffRules.CheckUrl(url));

    [Fact]
    public void Limits_Length_And_Cleans_Titles()
    {
        Assert.Null(HandoffRules.CheckUrl("https://example.com/" + new string('a', HandoffRules.MaxUrl)));
        Assert.NotNull(HandoffRules.CheckUrl("https://example.com/" + new string('a', 1000)));
        var link = HandoffRules.CheckUrl("https://example.com/", "Pay\u202Egnp.exe\u0007 here " + new string('x', 400));
        Assert.DoesNotContain("\u202E", link!.Title);
        Assert.DoesNotContain("\u0007", link.Title);
        Assert.True(link.Title!.Length <= HandoffRules.MaxTitle);
        Assert.Null(HandoffRules.CheckUrl("https://example.com/", "   ")!.Title);
        Assert.Null(HandoffRules.CheckText("   "));
        Assert.Null(HandoffRules.CheckText(new string('a', HandoffRules.MaxText + 1)));
        Assert.Equal("hello\nworld", HandoffRules.CheckText("hello\nworld"));
        Assert.DoesNotContain("\n", HandoffRules.Preview("line one\nline two"));
    }

    [Fact]
    public void Toast_Link_Commands_Are_Checked_Again()
    {
        var good = new NotificationToastCommand(NotificationToastCommand.OpenLink, "https://example.com/x").ToArguments();
        var parsed = NotificationToastCommand.Parse(good.ToDictionary(k => k.Key, v => v.Value));
        Assert.Equal(NotificationToastCommand.OpenLink, parsed.Kind);
        Assert.Equal("https://example.com/x", parsed.Key);
        foreach (var bad in new[] { "file:///C:/x.exe", "javascript:1" })
        {
            var args = new NotificationToastCommand(NotificationToastCommand.OpenLink, bad).ToArguments().ToDictionary(k => k.Key, v => v.Value);
            Assert.Equal(NotificationToastCommand.Open, NotificationToastCommand.Parse(args).Kind);
            args = new NotificationToastCommand(NotificationToastCommand.CopyLink, bad).ToArguments().ToDictionary(k => k.Key, v => v.Value);
            Assert.Equal(NotificationToastCommand.Open, NotificationToastCommand.Parse(args).Kind);
        }
        Assert.Equal(NotificationToastCommand.Open, NotificationToastCommand.Parse(new Dictionary<string, string> { ["bridge"] = "open-link" }).Kind);
    }

    // ------------------------------------------------------------------ from the phone

    [Fact]
    public async Task Links_And_Text_From_The_Phone_Are_Offered_And_Answered()
    {
        var handoff = new HandoffService(_host);
        var links = new List<(HandoffLink Link, bool Open, string Phone)>();
        var texts = new List<string>();
        handoff.LinkReceived += (l, o, p) => { lock (links) links.Add((l, o, p)); };
        handoff.TextReceived += (t, _) => { lock (texts) texts.Add(t); };
        var (ch, pcFeatures) = await PhoneAsync();
        await using (ch)
        {
            Assert.True(pcFeatures.Contains(Features.Handoff));
            Assert.Equal(HandoffStatus.Shown, (await Hand(ch, Url("a1", "https://example.com/page", "Example"))).Status);
            Assert.Equal(HandoffStatus.Copied, (await Hand(ch, Text("a2", "some text"))).Status);
            Assert.Equal(HandoffStatus.Invalid, (await Hand(ch, Url("a3", "file:///C:/Windows/notepad.exe"))).Status);
            Assert.Equal(HandoffStatus.Invalid, (await Hand(ch, new HandoffPayload("a4", "app") { Url = "https://example.com" })).Status);
            Assert.Equal(HandoffStatus.Invalid, (await Hand(ch, Text("a5", ""))).Status);
            // A retry of one already handled gets the same answer, and nothing shows twice.
            Assert.Equal(HandoffStatus.Shown, (await Hand(ch, Url("a1", "https://example.com/page", "Example"))).Status);
            lock (links)
            {
                var only = Assert.Single(links);
                Assert.Equal("https://example.com/page", only.Link.Url);
                Assert.Equal("Example", only.Link.Title);
                Assert.False(only.Open);
                Assert.Equal("Pixel", only.Phone);
            }
            lock (texts) Assert.Equal("some text", Assert.Single(texts));
        }
    }

    [Fact]
    public async Task Copies_Of_One_Id_Arriving_Together_Act_Once()
    {
        var handoff = new HandoffService(_host);
        var shown = 0;
        handoff.LinkReceived += (_, _, _) => { Interlocked.Increment(ref shown); Thread.Sleep(20); }; // widen any race
        var (ch, _) = await PhoneAsync();
        await using (ch)
        {
            for (var i = 0; i < 12; i++)
                await ch.SendAsync(BridgeMessage.Create(MessageType.HandoffOpen, Url("same", "https://example.com/once")));
            var acks = new List<string>();
            while (acks.Count < 12) acks.Add((await Next(ch, MessageType.HandoffAck)).PayloadAs<HandoffAckPayload>().Status);
            Assert.True(acks.All(s => s == HandoffStatus.Shown), string.Join(",", acks));
            Assert.Equal(1, shown);
        }
    }

    [Fact]
    public async Task Settings_Decide_Whether_Links_Open_Or_Anything_Is_Accepted()
    {
        var enabled = true;
        var autoOpen = true;
        var handoff = new HandoffService(_host, () => enabled, () => autoOpen);
        var opened = 0;
        handoff.LinkReceived += (_, open, _) => { if (open) Interlocked.Increment(ref opened); };
        var (ch, _) = await PhoneAsync();
        await using (ch)
        {
            Assert.Equal(HandoffStatus.Opened, (await Hand(ch, Url("b1", "https://example.com/"))).Status);
            Assert.Equal(1, opened);
            enabled = false;
            Assert.Equal(HandoffStatus.Disabled, (await Hand(ch, Url("b2", "https://example.com/"))).Status);
            Assert.Equal(HandoffStatus.Disabled, (await Hand(ch, Text("b3", "x"))).Status);
            Assert.Equal(1, opened);
        }
    }

    [Fact]
    public async Task A_Flood_Is_Refused_Until_The_Minute_Passes()
    {
        var now = DateTimeOffset.UtcNow;
        var handoff = new HandoffService(_host, now: () => now);
        var shown = 0;
        handoff.TextReceived += (_, _) => Interlocked.Increment(ref shown);
        var (ch, _) = await PhoneAsync();
        await using (ch)
        {
            for (var i = 0; i < HandoffService.MaxPerMinute; i++)
                Assert.Equal(HandoffStatus.Copied, (await Hand(ch, Text($"c{i}", $"t{i}"))).Status);
            Assert.Equal(HandoffStatus.Busy, (await Hand(ch, Text("c-over", "x"))).Status);
            Assert.Equal(HandoffService.MaxPerMinute, shown);
            now = now.AddSeconds(61);
            Assert.Equal(HandoffStatus.Copied, (await Hand(ch, Text("c-later", "x"))).Status);
        }
    }

    [Fact]
    public async Task Garbage_Without_An_Id_Is_Ignored_And_The_Session_Lives_On()
    {
        var handoff = new HandoffService(_host);
        var (ch, _) = await PhoneAsync();
        await using (ch)
        {
            await ch.SendAsync(BridgeMessage.Create(MessageType.HandoffOpen, new { kind = "url", url = "https://example.com" }));
            await ch.SendAsync(BridgeMessage.Create(MessageType.HandoffOpen, new HandoffPayload(new string('x', 65), HandoffKind.Text) { Text = "x" }));
            await ch.SendAsync(BridgeMessage.Create(MessageType.HandoffAck, new HandoffAckPayload("nobody-asked", HandoffStatus.Shown)));
            Assert.Equal(HandoffStatus.Copied, (await Hand(ch, Text("d1", "still here"))).Status);
        }
    }

    // ------------------------------------------------------------------ to the phone

    [Fact]
    public async Task Sending_Waits_For_The_Phones_Answer()
    {
        var handoff = new HandoffService(_host);
        var (ch, _) = await PhoneAsync();
        await using (ch)
        {
            var sending = handoff.SendUrlAsync("https://example.com/article", "An article");
            var got = (await Next(ch, MessageType.HandoffOpen)).PayloadAs<HandoffPayload>();
            Assert.Equal(HandoffKind.Url, got.Kind);
            Assert.Equal("https://example.com/article", got.Url);
            Assert.Equal("An article", got.Title);
            Assert.Null(got.Text);
            await ch.SendAsync(BridgeMessage.Create(MessageType.HandoffAck, new HandoffAckPayload(got.Id, HandoffStatus.Shown)));
            Assert.Equal(HandoffStatus.Shown, await sending.WithTimeout());

            // A lone link on the clipboard goes as a link; anything else as text.
            var clip = handoff.SendClipboardAsync("  https://example.com/x  ");
            var c = (await Next(ch, MessageType.HandoffOpen)).PayloadAs<HandoffPayload>();
            Assert.Equal(HandoffKind.Url, c.Kind);
            await ch.SendAsync(BridgeMessage.Create(MessageType.HandoffAck, new HandoffAckPayload(c.Id, HandoffStatus.Shown)));
            await clip.WithTimeout();
            clip = handoff.SendClipboardAsync("see https://example.com/x");
            c = (await Next(ch, MessageType.HandoffOpen)).PayloadAs<HandoffPayload>();
            Assert.Equal(HandoffKind.Text, c.Kind);
            Assert.Equal("see https://example.com/x", c.Text);
            await ch.SendAsync(BridgeMessage.Create(MessageType.HandoffAck, new HandoffAckPayload(c.Id, HandoffStatus.Disabled)));
            Assert.Equal(HandoffStatus.Disabled, await clip.WithTimeout());
            Assert.Contains("isn't accepting", HandoffService.Explain(HandoffStatus.Disabled));
            Assert.Null(HandoffService.Explain(HandoffStatus.Copied));

            Assert.Equal(HandoffStatus.Invalid, await handoff.SendUrlAsync("javascript:alert(1)"));
            Assert.Equal(HandoffStatus.Invalid, await handoff.SendTextAsync(""));
        }
    }

    [Fact]
    public async Task Nothing_Is_Sent_Without_A_Phone_That_Takes_Handoffs()
    {
        var handoff = new HandoffService(_host);
        Assert.Equal(HandoffStatus.Unavailable, await handoff.SendTextAsync("x").WithTimeout());
        var (ch, _) = await PhoneAsync(handoff: false);
        await using (ch)
        {
            Assert.Equal(HandoffStatus.Unavailable, await handoff.SendUrlAsync("https://example.com").WithTimeout());
            Assert.Contains("isn't connected", HandoffService.Explain(HandoffStatus.Unavailable));
        }
    }

    [Fact]
    public async Task The_Phone_Dropping_Mid_Send_Is_Unavailable_Not_A_Hang()
    {
        var handoff = new HandoffService(_host);
        var (ch, _) = await PhoneAsync();
        var sending = handoff.SendTextAsync("x");
        await Next(ch, MessageType.HandoffOpen);
        await ch.DisposeAsync();
        Assert.Equal(HandoffStatus.Unavailable, await sending.WaitAsync(HandoffService.AckTimeout + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task The_Log_Never_Holds_Links_Or_Text()
    {
        var handoff = new HandoffService(_host);
        var log = new DiagnosticsLog(null);
        _ = new BridgeDiagnostics(log, _host, _files, handoff: handoff);
        var (ch, _) = await PhoneAsync();
        await using (ch)
        {
            await Hand(ch, Url("e1", "https://secret-ZEBRA.example.com/token=ZEBRA", "ZEBRA title"));
            await Hand(ch, Text("e2", "ZEBRA password 1234"));
            await Hand(ch, Url("e3", "javascript:ZEBRA"));
            var sending = handoff.SendTextAsync("ZEBRA outgoing");
            var got = (await Next(ch, MessageType.HandoffOpen)).PayloadAs<HandoffPayload>();
            await ch.SendAsync(BridgeMessage.Create(MessageType.HandoffAck, new HandoffAckPayload(got.Id, "ZEBRA-status")));
            await sending.WithTimeout();
        }
        var lines = log.Lines();
        Assert.Contains(lines, l => l.Contains("handoff") && l.Contains("dir=in") && l.Contains("status=shown"));
        Assert.Contains(lines, l => l.Contains("handoff") && l.Contains("dir=out") && l.Contains("kind=text"));
        Assert.Contains(lines, l => l.Contains("status=invalid"));
        foreach (var l in lines) Assert.False(l.Contains("ZEBRA", StringComparison.OrdinalIgnoreCase) || l.Contains("example.com"), l);
    }
}
