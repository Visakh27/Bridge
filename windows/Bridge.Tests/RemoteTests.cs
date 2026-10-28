using System.Net;
using System.Net.Sockets;
using System.Text;
using Bridge.Core;
using Bridge.Network;
using Bridge.Protocol;
using Bridge.Relay;
using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

/// <summary>Phase I: the self-hosted relay and remote access through it.</summary>
public sealed class RemoteTests : IDisposable
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private readonly List<IAsyncDisposable> _cleanup = [];
    private readonly CancellationTokenSource _cts = new();

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var d in Enumerable.Reverse(_cleanup)) { try { d.DisposeAsync().AsTask().Wait(5000); } catch (Exception) { } }
    }

    private sealed class Disposer(Func<ValueTask> f) : IAsyncDisposable { public ValueTask DisposeAsync() => f(); }

    private (RelayServer Relay, RelayLink Link, TcpListener Listener) StartRelay(RelayOptions? options = null, byte[]? key = null, int port = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var relay = new RelayServer(key ?? Key, options);
        _ = relay.RunAsync(listener, _cts.Token);
        _cleanup.Add(relay);
        return (relay, new RelayLink("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, key ?? Key), listener);
    }

    private static string RoomOf(string seed) => RelayProtocol.Room(Encoding.UTF8.GetBytes("pc" + seed), Encoding.UTF8.GetBytes("phone" + seed));

    /// <summary>A raw relay client: sends <paramref name="hello"/> and returns the first reply line.</summary>
    private static async Task<(TcpClient Client, string? Reply)> Raw(RelayLink link, string hello)
    {
        var c = new TcpClient();
        await c.ConnectAsync(link.Host, link.Port);
        await RelayProtocol.WriteLineAsync(c.GetStream(), hello, CancellationToken.None);
        using var t = new CancellationTokenSource(5000);
        try { return (c, await RelayProtocol.ReadLineAsync(c.GetStream(), t.Token)); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { return (c, null); }
    }

    // ------------------------------------------------------------------ the relay link

    [Theory]
    [InlineData("bridge-relay://relay.example.com:47830#AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA", "relay.example.com", 47830)]
    [InlineData("  BRIDGE-RELAY://Relay.Example.com#AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA  ", "relay.example.com", 47830)]
    [InlineData("bridge-relay://203.0.113.7:9000/#AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA", "203.0.113.7", 9000)]
    [InlineData("bridge-relay://[2001:db8::7]:443#AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA", "2001:db8::7", 443)]
    public void Reads_Relay_Links(string text, string host, int port)
    {
        var link = RelayLink.Parse(text);
        Assert.NotNull(link);
        Assert.Equal(host, link!.Host);
        Assert.Equal(port, link.Port);
        Assert.Equal(Key, link.Key);
        Assert.Equal(link, RelayLink.Parse(link.ToString())); // round trip
        Assert.DoesNotContain(RelayLink.EncodeKey(Key), link.Display); // what's shown never has the key
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://relay.example.com#AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
    [InlineData("bridge-relay://relay.example.com:47830")]
    [InlineData("bridge-relay://relay.example.com#short")]
    [InlineData("bridge-relay://relay.example.com#AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=")]
    [InlineData("bridge-relay://relay.example.com#AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eH+A")]
    [InlineData("bridge-relay://relay.example.com:0#AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
    [InlineData("bridge-relay://relay.example.com:70000#AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
    [InlineData("bridge-relay://user@relay.example.com#AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
    [InlineData("bridge-relay://relay example.com#AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
    [InlineData("bridge-relay://relay.example.com/x#AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
    [InlineData("bridge-relay://[not-ipv6]:1#AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
    [InlineData("bridge-relay://-bad-.example.com#AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
    public void Refuses_Broken_Relay_Links(string text) => Assert.Null(RelayLink.Parse(text));

    // ------------------------------------------------------------------ the join protocol

    [Fact]
    public void Rooms_And_Proofs_Match_The_Published_Vectors()
    {
        // Same vectors as android RemoteTest (and an independent Python check of the spec): both platforms must agree byte for byte.
        var room = RelayProtocol.Room(Encoding.ASCII.GetBytes("PCKEY"), Encoding.ASCII.GetBytes("PHONEKEY"));
        Assert.Equal(64, room.Length);
        Assert.Equal(room, RelayProtocol.Room(Encoding.ASCII.GetBytes("PCKEY"), Encoding.ASCII.GetBytes("PHONEKEY")));
        Assert.NotEqual(room, RelayProtocol.Room(Encoding.ASCII.GetBytes("PHONEKEY"), Encoding.ASCII.GetBytes("PCKEY"))); // order matters
        Assert.NotEqual(room, RelayProtocol.Room(Encoding.ASCII.GetBytes("PCKE"), Encoding.ASCII.GetBytes("YPHONEKEY"))); // separators matter
        Assert.Equal(RelayVectors.Room, room);
        Assert.Equal(RelayVectors.Proof, RelayProtocol.Proof(Key, RelayRole.Phone, room, 1_700_000_000, "00112233445566778899aabbccddeeff"));
    }

    [Fact]
    public void Hellos_Are_Checked_For_Shape_Key_And_Time()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var room = RoomOf("a");
        var good = RelayProtocol.ParseHello(RelayProtocol.Hello(Key, RelayRole.Pc, room, now))!;
        Assert.True(RelayProtocol.Verify(Key, good, now));
        Assert.True(RelayProtocol.Verify(Key, good, now.AddSeconds(100)));
        Assert.False(RelayProtocol.Verify(Key, good, now.AddMinutes(3))); // too old
        Assert.False(RelayProtocol.Verify(Key, good, now.AddMinutes(-3))); // from the future
        var otherKey = (byte[])Key.Clone(); otherKey[0] ^= 1;
        Assert.False(RelayProtocol.Verify(otherKey, good, now));
        Assert.False(RelayProtocol.Verify(Key, good with { Role = RelayRole.Phone }, now)); // the role is signed
        Assert.False(RelayProtocol.Verify(Key, good with { Room = RoomOf("b") }, now));

        foreach (var bad in new[]
        {
            "", "not json", "[]", "{}",
            RelayProtocol.Hello(Key, "admin", room, now),
            RelayProtocol.Hello(Key, RelayRole.Pc, "ROOM", now),
            RelayProtocol.Hello(Key, RelayRole.Pc, room, now, nonce: "xyz"),
            RelayProtocol.Hello(Key, RelayRole.Pc, room, now).Replace("\"v\":1", "\"v\":2"),
            RelayProtocol.Hello(Key, RelayRole.Pc, room, now).Replace("}", ",\"extra\":1}"),
        })
            Assert.Null(RelayProtocol.ParseHello(bad));
    }

    // ------------------------------------------------------------------ the relay itself

    [Fact]
    public async Task A_Waiting_PC_And_Its_Phone_Get_A_Pipe_Both_Ways()
    {
        var (relay, link, _) = StartRelay();
        var room = RoomOf("1");
        var waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pcJoin = RelayClient.JoinAsync(link, RelayRole.Pc, room, _cts.Token, waiting: () => waited.TrySetResult());
        await waited.Task.WithTimeout();
        Assert.Equal(1, relay.WaitingPcs);
        using var phone = await RelayClient.JoinAsync(link, RelayRole.Phone, room, _cts.Token).WithTimeout();
        using var pc = await pcJoin.WithTimeout();
        Assert.Equal(0, relay.WaitingPcs);

        var big = new byte[300_000]; new Random(3).NextBytes(big);
        var send = phone.GetStream().WriteAsync(big).AsTask();
        var got = new byte[big.Length];
        await pc.GetStream().ReadExactlyAsync(got).AsTask().WithTimeout();
        await send;
        Assert.Equal(big, got);
        await pc.GetStream().WriteAsync("back"u8.ToArray());
        var back = new byte[4];
        await phone.GetStream().ReadExactlyAsync(back).AsTask().WithTimeout();
        Assert.Equal("back"u8.ToArray(), back);

        pc.Close(); // one side leaves: the other's end closes too
        var buf = new byte[1];
        Assert.Equal(0, await phone.GetStream().ReadAsync(buf).AsTask().WithTimeout());
    }

    [Fact]
    public async Task The_Relay_Turns_Away_What_It_Should()
    {
        var (relay, link, _) = StartRelay();
        var now = DateTimeOffset.UtcNow;

        var (c1, r1) = await Raw(link, RelayProtocol.Hello(Key, RelayRole.Phone, RoomOf("x"), now));
        Assert.Equal((("error", RelayError.PcOffline)), RelayProtocol.ParseReply(r1!)); // nobody waiting in that room
        var (c2, r2) = await Raw(link, "{\"hello\":true}");
        Assert.Equal(("error", RelayError.BadHello), RelayProtocol.ParseReply(r2!));
        var wrong = new byte[32];
        var (c3, r3) = await Raw(link, RelayProtocol.Hello(wrong, RelayRole.Pc, RoomOf("x"), now));
        Assert.Equal(("error", RelayError.Unauthorized), RelayProtocol.ParseReply(r3!));
        var stale = RelayProtocol.Hello(Key, RelayRole.Pc, RoomOf("x"), now.AddMinutes(-10));
        var (c4, r4) = await Raw(link, stale);
        Assert.Equal(("error", RelayError.Unauthorized), RelayProtocol.ParseReply(r4!));

        // A captured hello can't be used again.
        var hello = RelayProtocol.Hello(Key, RelayRole.Pc, RoomOf("y"), now);
        var (c5, r5) = await Raw(link, hello);
        Assert.Equal("waiting", RelayProtocol.ParseReply(r5!).Type);
        var (c6, r6) = await Raw(link, hello);
        Assert.Equal(("error", RelayError.Unauthorized), RelayProtocol.ParseReply(r6!));

        // A line that never ends, and silence, are both cut off.
        var c7 = new TcpClient(); await c7.ConnectAsync(link.Host, link.Port);
        await c7.GetStream().WriteAsync(new byte[2000].Select(_ => (byte)'a').ToArray());
        using var t = new CancellationTokenSource(5000);
        Assert.Equal(("error", RelayError.BadHello), RelayProtocol.ParseReply((await RelayProtocol.ReadLineAsync(c7.GetStream(), t.Token))!));
        foreach (var c in new[] { c1, c2, c3, c4, c5, c6, c7 }) c.Dispose();
        for (var i = 0; i < 100 && relay.Connections > 0; i++) await Task.Delay(20);
        Assert.Equal(0, relay.Connections);
    }

    [Fact]
    public async Task A_Silent_Client_Is_Dropped_After_The_Hello_Timeout()
    {
        var (relay, link, _) = StartRelay(new RelayOptions { HelloTimeout = TimeSpan.FromMilliseconds(300) });
        using var c = new TcpClient();
        await c.ConnectAsync(link.Host, link.Port);
        using var t = new CancellationTokenSource(5000);
        var line = await RelayProtocol.ReadLineAsync(c.GetStream(), t.Token);
        Assert.Equal(("error", RelayError.BadHello), RelayProtocol.ParseReply(line!));
    }

    [Fact]
    public async Task The_Newest_PC_Connection_Takes_The_Room_And_Waiting_PCs_Are_Pinged()
    {
        var (relay, link, _) = StartRelay(new RelayOptions { PingInterval = TimeSpan.FromMilliseconds(150) });
        var room = RoomOf("2");
        var (old, first) = await Raw(link, RelayProtocol.Hello(Key, RelayRole.Pc, room, DateTimeOffset.UtcNow));
        Assert.Equal("waiting", RelayProtocol.ParseReply(first!).Type);
        using (var t = new CancellationTokenSource(5000))
            Assert.Equal("ping", RelayProtocol.ParseReply((await RelayProtocol.ReadLineAsync(old.GetStream(), t.Token))!).Type);

        var waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newer = RelayClient.JoinAsync(link, RelayRole.Pc, room, _cts.Token, waiting: () => waited.TrySetResult());
        await waited.Task.WithTimeout();
        string? line;
        using (var t = new CancellationTokenSource(5000))
            do line = await RelayProtocol.ReadLineAsync(old.GetStream(), t.Token); while (line is not null && RelayProtocol.ParseReply(line).Type == "ping");
        Assert.Equal(("error", RelayError.Replaced), RelayProtocol.ParseReply(line!));
        Assert.Equal(1, relay.WaitingPcs);

        using var phone = await RelayClient.JoinAsync(link, RelayRole.Phone, room, _cts.Token).WithTimeout();
        using var pc = await newer.WithTimeout(); // the phone got the newest PC connection
        old.Dispose();
    }

    [Fact]
    public async Task Each_Address_And_The_Whole_Relay_Have_A_Connection_Limit()
    {
        var (relay, link, _) = StartRelay(new RelayOptions { MaxConnectionsPerAddress = 3 });
        var open = new List<TcpClient>();
        for (var i = 0; i < 3; i++)
        {
            var (c, r) = await Raw(link, RelayProtocol.Hello(Key, RelayRole.Pc, RoomOf($"lim{i}"), DateTimeOffset.UtcNow));
            Assert.Equal("waiting", RelayProtocol.ParseReply(r!).Type);
            open.Add(c);
        }
        var (over, reply) = await Raw(link, RelayProtocol.Hello(Key, RelayRole.Pc, RoomOf("lim9"), DateTimeOffset.UtcNow));
        Assert.Equal(("error", RelayError.Busy), RelayProtocol.ParseReply(reply!));
        over.Dispose();
        open[0].Dispose();
        for (var i = 0; i < 100 && relay.Connections > 2; i++) await Task.Delay(20);
        var (again, r2) = await Raw(link, RelayProtocol.Hello(Key, RelayRole.Pc, RoomOf("lim0"), DateTimeOffset.UtcNow));
        Assert.Equal("waiting", RelayProtocol.ParseReply(r2!).Type);
        foreach (var c in open.Append(again)) c.Dispose();
    }

    [Fact]
    public async Task An_Idle_Pipe_Is_Closed()
    {
        var (relay, link, _) = StartRelay(new RelayOptions { IdleTimeout = TimeSpan.FromMilliseconds(500) });
        var room = RoomOf("idle");
        var waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pcJoin = RelayClient.JoinAsync(link, RelayRole.Pc, room, _cts.Token, waiting: () => waited.TrySetResult());
        await waited.Task.WithTimeout();
        using var phone = await RelayClient.JoinAsync(link, RelayRole.Phone, room, _cts.Token);
        using var pc = await pcJoin;
        var buf = new byte[1];
        Assert.Equal(0, await phone.GetStream().ReadAsync(buf).AsTask().WithTimeout());
        for (var i = 0; i < 100 && relay.Pipes > 0; i++) await Task.Delay(20);
        Assert.Equal(0, relay.Pipes);
    }

    [Fact]
    public async Task A_Relay_That_Stops_Answering_Is_Noticed()
    {
        // A "relay" that accepts and then says nothing.
        var mute = new TcpListener(IPAddress.Loopback, 0);
        mute.Start();
        _ = mute.AcceptTcpClientAsync();
        var link = new RelayLink("127.0.0.1", ((IPEndPoint)mute.LocalEndpoint).Port, Key);
        await Assert.ThrowsAsync<IOException>(() => RelayClient.JoinAsync(link, RelayRole.Pc, RoomOf("m"), _cts.Token, lineTimeout: TimeSpan.FromMilliseconds(300)));
        mute.Stop();
    }

    // ------------------------------------------------------------------ remote access on the PC

    private sealed class NoClipboard : IClipboardAccess
    {
        public event Action<string>? LocalClipboardChanged { add { } remove { } }
        public Task<string?> ReadTextAsync() => Task.FromResult<string?>(null);
        public Task WriteTextAsync(string text) => Task.CompletedTask;
    }

    private sealed class Pc
    {
        public DeviceIdentity Identity { get; } = DeviceIdentity.Generate();
        public InMemoryPairedDeviceStore Store { get; } = new();
        public TcpBridgeListener Listener { get; } = new(0);
        public PairingService Pairing { get; }
        public BridgeHost Host { get; }
        public RemoteAccess Remote { get; }
        public DiagnosticsLog Log { get; } = new(null);
        public Pc()
        {
            Pairing = new PairingService(Identity, Store, "PC");
            var files = new FileTransferService();
            Host = new BridgeHost(Identity, Store, Pairing, files, new ClipboardSyncService(new NoClipboard()), Loopback.TempDir());
            Remote = new RemoteAccess(Host, Store, Identity, backoff: [TimeSpan.FromMilliseconds(100)], refusedBackoff: TimeSpan.FromMilliseconds(300), rescan: TimeSpan.FromMilliseconds(200));
            _ = new BridgeDiagnostics(Log, Host, files);
            Remote.Completed += (ev, code) => Log.Info(ev, LogField.Code("code", code));
        }
    }

    private Pc StartPc()
    {
        var pc = new Pc();
        _ = pc.Host.RunAsync(pc.Listener, _cts.Token);
        _cleanup.Add(new Disposer(async () => { await pc.Remote.DisposeAsync(); await pc.Host.DisposeAsync(); pc.Listener.Dispose(); }));
        return pc;
    }

    private static async Task<DeviceIdentity> PairPhone(Pc pc)
    {
        var phone = DeviceIdentity.Generate();
        var qr = pc.Pairing.CreateQrPayload(pc.Listener.Port, "127.0.0.1");
        var t = await TcpTransport.ConnectAsync("127.0.0.1", pc.Listener.Port);
        await using var ch = await EncryptedChannel.HandshakeAsInitiatorAsync(t, phone, pc.Identity.PublicKeyBlob).WithTimeout();
        await ch.SendAsync(BridgeMessage.Create(MessageType.PairingRequest,
            new PairingRequestPayload(qr.PairingToken, "Pixel", Convert.ToBase64String(phone.PublicKeyBlob))));
        Assert.Equal(MessageType.PairingAccept, (await ch.ReceiveAsync().WithTimeout()).Type);
        return phone;
    }

    /// <summary>The phone's side through the relay: join, handshake, capabilities.</summary>
    private static async Task<EncryptedChannel> ConnectViaRelay(RelayLink link, Pc pc, DeviceIdentity phone, string[] features)
    {
        var room = RelayProtocol.Room(pc.Identity.PublicKeyBlob, phone.PublicKeyBlob);
        var client = await RelayClient.JoinAsync(link, RelayRole.Phone, room, CancellationToken.None).WithTimeout();
        var ch = await EncryptedChannel.HandshakeAsInitiatorAsync(new TcpTransport(client), phone, pc.Identity.PublicKeyBlob).WithTimeout();
        await ch.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload(features)));
        var reply = await ch.ReceiveAsync().WithTimeout();
        Assert.Equal(MessageType.CapabilitiesAdvertise, reply.Type);
        return ch;
    }

    private static async Task WaitFor(Func<bool> condition, int ms = 8000)
    {
        for (var i = 0; i < ms / 20 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    private static async Task<BridgeMessage> Next(EncryptedChannel ch, string type)
    {
        while (true) { var m = await ch.ReceiveAsync().WithTimeout(); if (m.Type == type) return m; }
    }

    [Fact]
    public async Task A_Paired_Phone_Reaches_The_PC_Through_The_Relay()
    {
        var (relay, link, _) = StartRelay();
        var pc = StartPc();
        var phone = await PairPhone(pc);
        var states = new List<RemoteState>();
        pc.Remote.StatusChanged += s => { lock (states) states.Add(s.State); };
        Assert.Equal(RemoteState.Off, pc.Remote.Status.State);
        await pc.Remote.ConfigureAsync(link);
        await WaitFor(() => pc.Remote.Status.State == RemoteState.Ready && relay.WaitingPcs == 1);

        await using var ch = await ConnectViaRelay(link, pc, phone, [Features.Files, Features.Remote]);
        await WaitFor(() => pc.Host.Sessions.Count == 1);
        Assert.Equal(ConnectionRoute.Relay, pc.Host.Sessions[0].Device.Route);
        // The PC tells the phone about the relay (so a phone at home learns it) — even over the relay.
        var config = (await Next(ch, MessageType.RelayConfig)).PayloadAs<RelayConfigPayload>();
        Assert.True(config.Enabled);
        Assert.Equal(link.Port, config.Port);
        Assert.Equal(RelayLink.EncodeKey(Key), config.Key);
        await WaitFor(() => relay.WaitingPcs == 1); // already waiting for the next time
        lock (states) Assert.Equal(RemoteState.Connecting, states[0]);
        // The log's own "started" handler may run after the one that sent relay.config: give it a moment.
        await WaitFor(() => pc.Log.Lines().Any(l => l.Contains("session.started") && l.Contains("route=relay")));
    }

    [Fact]
    public async Task With_No_Phone_Paired_Remote_Access_Says_So()
    {
        var (relay, link, _) = StartRelay();
        var pc = StartPc();
        await pc.Remote.ConfigureAsync(link);
        await WaitFor(() => pc.Remote.Status == new RemoteStatus(RemoteState.Ready, RemoteStatus.NoPhones));
        Assert.Contains("Pair a phone", pc.Remote.Status.Describe(link.Display));
        Assert.Equal(0, relay.WaitingPcs);
        await PairPhone(pc); // picked up without restarting anything
        await WaitFor(() => pc.Remote.Status == new RemoteStatus(RemoteState.Ready) && relay.WaitingPcs == 1);
    }

    [Fact]
    public async Task Turning_Remote_Access_Off_Tells_The_Phone_And_Drops_Relay_Sessions()
    {
        var (relay, link, _) = StartRelay();
        var pc = StartPc();
        var phone = await PairPhone(pc);
        await pc.Remote.ConfigureAsync(link);
        await WaitFor(() => relay.WaitingPcs == 1);
        await using var viaRelay = await ConnectViaRelay(link, pc, phone, [Features.Files, Features.Remote]);
        await Next(viaRelay, MessageType.RelayConfig);
        await WaitFor(() => pc.Host.Sessions.Count == 1);

        await pc.Remote.ConfigureAsync(null);
        Assert.Equal(RemoteState.Off, pc.Remote.Status.State);
        await WaitFor(() => pc.Host.Sessions.Count == 0); // the relay session is gone
        await WaitFor(() => relay.WaitingPcs == 0); // and the PC no longer waits there
        await Task.Delay(500);
        Assert.Equal(0, relay.WaitingPcs);

        // On the local network, a phone that supports remote access hears it's off.
        var t = await TcpTransport.ConnectAsync("127.0.0.1", pc.Listener.Port);
        await using var lan = await EncryptedChannel.HandshakeAsInitiatorAsync(t, phone, pc.Identity.PublicKeyBlob).WithTimeout();
        await lan.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload([Features.Files, Features.Remote])));
        await lan.ReceiveAsync().WithTimeout();
        Assert.False((await Next(lan, MessageType.RelayConfig)).PayloadAs<RelayConfigPayload>().Enabled);
        Assert.Equal(ConnectionRoute.Lan, pc.Host.Sessions.Single().Device.Route);
    }

    [Fact]
    public async Task Phones_Without_Remote_Support_Never_Get_The_Relay_Key()
    {
        var (_, link, _) = StartRelay();
        var pc = StartPc();
        var phone = await PairPhone(pc);
        await pc.Remote.ConfigureAsync(link);
        var t = await TcpTransport.ConnectAsync("127.0.0.1", pc.Listener.Port);
        await using var lan = await EncryptedChannel.HandshakeAsInitiatorAsync(t, phone, pc.Identity.PublicKeyBlob).WithTimeout();
        await lan.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload([Features.Files])));
        await lan.ReceiveAsync().WithTimeout();
        using var quiet = new CancellationTokenSource(800);
        try { while (true) Assert.NotEqual(MessageType.RelayConfig, (await lan.ReceiveAsync(quiet.Token)).Type); }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Pairing_Through_The_Relay_Is_Refused()
    {
        var (relay, link, _) = StartRelay();
        var pc = StartPc();
        var phone = await PairPhone(pc); // so the PC waits in that phone's room
        await pc.Remote.ConfigureAsync(link);
        await WaitFor(() => relay.WaitingPcs == 1);
        var room = RelayProtocol.Room(pc.Identity.PublicKeyBlob, phone.PublicKeyBlob);
        var client = await RelayClient.JoinAsync(link, RelayRole.Phone, room, CancellationToken.None).WithTimeout();
        await using var ch = await EncryptedChannel.HandshakeAsInitiatorAsync(new TcpTransport(client), DeviceIdentity.Generate(), pc.Identity.PublicKeyBlob).WithTimeout();
        var qr = pc.Pairing.CreateQrPayload(pc.Listener.Port, "127.0.0.1"); // even with a valid code
        await ch.SendAsync(BridgeMessage.Create(MessageType.PairingRequest,
            new PairingRequestPayload(qr.PairingToken, "Intruder", Convert.ToBase64String(DeviceIdentity.Generate().PublicKeyBlob))));
        var reply = await ch.ReceiveAsync().WithTimeout();
        Assert.Equal(MessageType.Error, reply.Type);
        Assert.Equal(ErrorCodes.PermissionDenied, reply.PayloadAs<ErrorPayload>().Code);
        Assert.Single(await pc.Store.ListAsync());
        await WaitFor(() => pc.Log.Lines().Any(l => l.Contains("pairing_via_relay")));
    }

    [Fact]
    public async Task Someone_Elses_Phone_Gets_Nowhere_Through_The_Relay()
    {
        var (relay, link, _) = StartRelay();
        var pc = StartPc();
        var phone = await PairPhone(pc);
        await pc.Remote.ConfigureAsync(link);
        await WaitFor(() => relay.WaitingPcs == 1);
        // Knows the relay link and even the room, but not the phone's private key.
        var room = RelayProtocol.Room(pc.Identity.PublicKeyBlob, phone.PublicKeyBlob);
        var client = await RelayClient.JoinAsync(link, RelayRole.Phone, room, CancellationToken.None).WithTimeout();
        var stranger = DeviceIdentity.Generate();
        await using var ch = await EncryptedChannel.HandshakeAsInitiatorAsync(new TcpTransport(client), stranger, pc.Identity.PublicKeyBlob).WithTimeout();
        await ch.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload([Features.Files])));
        var reply = await ch.ReceiveAsync().WithTimeout();
        Assert.Equal(MessageType.Error, reply.Type);
        Assert.Empty(pc.Host.Sessions);
    }

    [Fact]
    public async Task A_Wrong_Key_Is_Reported_And_A_Dead_Relay_Is_Retried_Until_It_Is_Back()
    {
        var pc = StartPc();
        await PairPhone(pc);
        var (_, link, _) = StartRelay(key: Enumerable.Repeat((byte)9, 32).ToArray());
        await pc.Remote.ConfigureAsync(link with { Key = Key }); // the link has the wrong key
        await WaitFor(() => pc.Remote.Status.State == RemoteState.Refused);
        Assert.Equal(RelayError.Unauthorized, pc.Remote.Status.Code);
        Assert.Contains("key", pc.Remote.Status.Describe(link.Display));

        // A relay that isn't running yet: unreachable, then ready once it starts on that port.
        var spare = new TcpListener(IPAddress.Loopback, 0); spare.Start();
        var port = ((IPEndPoint)spare.LocalEndpoint).Port; spare.Stop();
        await pc.Remote.ConfigureAsync(new RelayLink("127.0.0.1", port, Key));
        await WaitFor(() => pc.Remote.Status.State == RemoteState.Unreachable);
        var (relay, _, _) = StartRelay(port: port);
        await WaitFor(() => pc.Remote.Status.State == RemoteState.Ready && relay.WaitingPcs == 1);
    }

    [Fact]
    public async Task Each_Paired_Phone_Has_Its_Own_Room_And_A_Forgotten_Phone_Stops_Waiting()
    {
        var (relay, link, _) = StartRelay();
        var pc = StartPc();
        var a = await PairPhone(pc);
        var b = await PairPhone(pc);
        await pc.Remote.ConfigureAsync(link);
        await WaitFor(() => relay.WaitingPcs == 2);
        var c = await PairPhone(pc); // paired later: picked up
        await WaitFor(() => relay.WaitingPcs == 3);
        await pc.Host.RevokeAsync(b.DeviceId);
        // b's waiting connection is left at the relay until it's used or the PC dials again; a phone
        // that comes for it finds a PC that no longer knows it.
        var room = RelayProtocol.Room(pc.Identity.PublicKeyBlob, b.PublicKeyBlob);
        try
        {
            var client = await RelayClient.JoinAsync(link, RelayRole.Phone, room, CancellationToken.None).WithTimeout();
            await using var ch = await EncryptedChannel.HandshakeAsInitiatorAsync(new TcpTransport(client), b, pc.Identity.PublicKeyBlob).WithTimeout();
            await ch.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload([Features.Files])));
            var reply = await ch.ReceiveAsync().WithTimeout();
            Assert.Equal(ErrorCodes.DeviceUnpaired, reply.PayloadAs<ErrorPayload>().Code);
        }
        catch (RelayRefusedException ex) { Assert.Equal(RelayError.PcOffline, ex.Code); }
        await Task.Delay(800);
        Assert.Equal(2, relay.WaitingPcs); // nobody waits in b's room again
        await using var viaA = await ConnectViaRelay(link, pc, a, [Features.Files]);
        await using var viaC = await ConnectViaRelay(link, pc, c, [Features.Files]);
        await WaitFor(() => pc.Host.Sessions.Count == 2);
    }

    [Fact]
    public async Task The_Log_Never_Holds_The_Relay_Address_Key_Or_Rooms()
    {
        var (relay, link, _) = StartRelay();
        var pc = StartPc();
        var phone = await PairPhone(pc);
        var relayLog = new List<string>();
        relay.Log += (e, d) => { lock (relayLog) relayLog.Add($"{e} {d}"); };
        await pc.Remote.ConfigureAsync(link);
        await WaitFor(() => relay.WaitingPcs == 1);
        await using (await ConnectViaRelay(link, pc, phone, [Features.Files])) { await WaitFor(() => pc.Host.Sessions.Count == 1); }
        await pc.Remote.ConfigureAsync(link with { Key = new byte[32] });
        await WaitFor(() => pc.Remote.Status.State == RemoteState.Refused);
        var room = RelayProtocol.Room(pc.Identity.PublicKeyBlob, phone.PublicKeyBlob);
        var secrets = new[] { "127.0.0.1", link.Port.ToString(), RelayLink.EncodeKey(Key), room, room[..16] };
        foreach (var l in pc.Log.Lines()) foreach (var s in secrets) Assert.False(l.Contains(s), $"PC log: {l}");
        lock (relayLog)
        {
            Assert.Contains(relayLog, l => l.StartsWith("relay.paired"));
            foreach (var l in relayLog) foreach (var s in secrets.Skip(2)) Assert.False(l.Contains(s), $"relay log: {l}");
        }
    }
}

/// <summary>Cross-platform test vectors (see android RemoteTest).</summary>
internal static class RelayVectors
{
    public const string Room = "97c559c85be584f787dd62067d66f898b61169f327a8e60e838813fd76135f20";
    public const string Proof = "6c0d263c2159a79e732516c2469caf91b8b1d8ec9782ec1bc667c781f559173b";
}
