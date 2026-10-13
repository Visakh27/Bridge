using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bridge.Network;
using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

/// <summary>Phase A: PC-side local discovery.</summary>
public class DiscoveryTests
{
    private static byte[] Nonce() => RandomNumberGenerator.GetBytes(DiscoveryWire.NonceSize);

    [Fact]
    public void Responder_Answers_Its_Own_Probe_With_A_Verifiable_Signed_Port()
    {
        var pc = DeviceIdentity.Generate();
        using var responder = new DiscoveryResponder(pc, () => 51234, udpPort: 0);
        var nonce = Nonce();

        var reply = responder.HandleDatagram(DiscoveryWire.BuildProbe(pc.PublicKeyBlob, nonce));

        Assert.NotNull(reply);
        Assert.Equal(51234, DiscoveryWire.VerifyReply(reply!, nonce, pc.DeviceId, pc.PublicKeyBlob));
        Assert.True(reply!.Length <= DiscoveryWire.MaxDatagram);
    }

    [Fact]
    public void Responder_Stays_Silent_For_Probes_Meant_For_Another_PC()
    {
        // Privacy: an observer who doesn't know this PC's key learns nothing,
        // and two Bridge PCs on one network never answer for each other.
        var pc = DeviceIdentity.Generate();
        var otherPc = DeviceIdentity.Generate();
        using var responder = new DiscoveryResponder(pc, () => 1, udpPort: 0);
        Assert.Null(responder.HandleDatagram(DiscoveryWire.BuildProbe(otherPc.PublicKeyBlob, Nonce())));
    }

    [Fact]
    public void Responder_Ignores_Malformed_Oversized_And_Wrong_Version_Datagrams()
    {
        var pc = DeviceIdentity.Generate();
        using var responder = new DiscoveryResponder(pc, () => 1, udpPort: 0);
        var good = JsonNode.Parse(DiscoveryWire.BuildProbe(pc.PublicKeyBlob, Nonce()))!.AsObject();

        byte[] Mutate(Action<JsonObject> change)
        {
            var copy = JsonNode.Parse(good.ToJsonString())!.AsObject();
            change(copy);
            return Encoding.UTF8.GetBytes(copy.ToJsonString());
        }

        Assert.Null(responder.HandleDatagram("not json"u8.ToArray()));
        Assert.Null(responder.HandleDatagram(new byte[DiscoveryWire.MaxDatagram + 1]));
        Assert.Null(responder.HandleDatagram(Mutate(o => o["v"] = 2)));
        Assert.Null(responder.HandleDatagram(Mutate(o => o["bridge"] = "here")));
        Assert.Null(responder.HandleDatagram(Mutate(o => o["nonce"] = "%%%")));
        Assert.Null(responder.HandleDatagram(Mutate(o => o["nonce"] = Convert.ToBase64String(new byte[8]))));
        Assert.Null(responder.HandleDatagram(Mutate(o => o.Remove("tag"))));
    }

    [Fact]
    public void Reply_Verification_Rejects_Forgeries_And_Replays()
    {
        var pc = DeviceIdentity.Generate();
        var attacker = DeviceIdentity.Generate(pc.DeviceId);
        using var real = new DiscoveryResponder(pc, () => 47820, udpPort: 0);
        using var fake = new DiscoveryResponder(attacker, () => 6666, udpPort: 0);
        var nonce = Nonce();
        var genuine = real.HandleDatagram(DiscoveryWire.BuildProbe(pc.PublicKeyBlob, nonce))!;

        // A reply signed by a different key, even with the right device id.
        var forged = fake.HandleDatagram(DiscoveryWire.BuildProbe(attacker.PublicKeyBlob, nonce))!;
        Assert.Null(DiscoveryWire.VerifyReply(forged, nonce, pc.DeviceId, pc.PublicKeyBlob));

        // An old genuine reply replayed against a fresh probe.
        Assert.Null(DiscoveryWire.VerifyReply(genuine, Nonce(), pc.DeviceId, pc.PublicKeyBlob));

        // Changing the port invalidates the signature.
        var tampered = JsonNode.Parse(genuine)!.AsObject();
        tampered["port"] = 6666;
        Assert.Null(DiscoveryWire.VerifyReply(Encoding.UTF8.GetBytes(tampered.ToJsonString()), nonce, pc.DeviceId, pc.PublicKeyBlob));

        Assert.Equal(47820, DiscoveryWire.VerifyReply(genuine, nonce, pc.DeviceId, pc.PublicKeyBlob));
    }

    [Fact]
    public void Responder_Rate_Limits_Replies()
    {
        var pc = DeviceIdentity.Generate();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var responder = new DiscoveryResponder(pc, () => 1, udpPort: 0, time: clock);

        var answered = Enumerable.Range(0, 100).Count(_ => responder.HandleDatagram(DiscoveryWire.BuildProbe(pc.PublicKeyBlob, Nonce())) is not null);
        Assert.Equal(DiscoveryResponder.MaxRepliesPerSecond, answered);

        clock.Now += TimeSpan.FromSeconds(1);
        Assert.NotNull(responder.HandleDatagram(DiscoveryWire.BuildProbe(pc.PublicKeyBlob, Nonce())));
    }

    [Fact]
    public void Reply_Reports_The_Current_Port_At_Reply_Time()
    {
        // If 47820 was taken and the listener fell back to another port, or it
        // changed after a restart, discovery must report the live one.
        var pc = DeviceIdentity.Generate();
        var port = 47820;
        using var responder = new DiscoveryResponder(pc, () => port, udpPort: 0);
        port = 50001;
        var nonce = Nonce();
        Assert.Equal(50001, DiscoveryWire.VerifyReply(responder.HandleDatagram(DiscoveryWire.BuildProbe(pc.PublicKeyBlob, nonce))!, nonce, pc.DeviceId, pc.PublicKeyBlob));
    }

    [Fact]
    public async Task Udp_Round_Trip_Over_Loopback()
    {
        var pc = DeviceIdentity.Generate();
        using var responder = new DiscoveryResponder(pc, () => 47820, udpPort: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = responder.RunAsync(cts.Token);

        using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        // Noise first: a probe for another PC and garbage, which must not stop the loop.
        await phone.SendAsync(DiscoveryWire.BuildProbe(DeviceIdentity.Generate().PublicKeyBlob, Nonce()), new IPEndPoint(IPAddress.Loopback, responder.Port));
        await phone.SendAsync("junk"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, responder.Port));
        var nonce = Nonce();
        await phone.SendAsync(DiscoveryWire.BuildProbe(pc.PublicKeyBlob, nonce), new IPEndPoint(IPAddress.Loopback, responder.Port));

        var reply = await phone.ReceiveAsync(cts.Token);
        Assert.Equal(47820, DiscoveryWire.VerifyReply(reply.Buffer, nonce, pc.DeviceId, pc.PublicKeyBlob));

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5)); // RunAsync never throws
    }

    [Fact]
    public void Second_Responder_On_The_Same_Port_Fails_Clearly()
    {
        // The app catches this and keeps working without discovery.
        var pc = DeviceIdentity.Generate();
        using var first = new DiscoveryResponder(pc, () => 1, udpPort: 0);
        Assert.Throws<SocketException>(() => new DiscoveryResponder(pc, () => 1, udpPort: first.Port).Dispose());
    }

    [Fact]
    public void Wire_Format_Matches_The_Spec()
    {
        var pc = DeviceIdentity.Generate();
        var probe = JsonNode.Parse(DiscoveryWire.BuildProbe(pc.PublicKeyBlob, Nonce()))!.AsObject();
        Assert.Equal(new[] { "bridge", "v", "nonce", "tag" }, probe.Select(p => p.Key).ToArray());
        Assert.Equal("discover", (string)probe["bridge"]!);

        using var responder = new DiscoveryResponder(pc, () => 1, udpPort: 0);
        var reply = JsonNode.Parse(responder.HandleDatagram(DiscoveryWire.BuildProbe(pc.PublicKeyBlob, Nonce()))!)!.AsObject();
        Assert.Equal(new[] { "bridge", "v", "nonce", "deviceId", "port", "sig" }, reply.Select(p => p.Key).ToArray());
        Assert.Equal("here", (string)reply["bridge"]!);
    }

    [Fact]
    public async Task Probe_Flood_Is_Throttled_And_Legit_Probes_Still_Get_Through_After()
    {
        var pc = DeviceIdentity.Generate();
        using var responder = new DiscoveryResponder(pc, () => 47820, udpPort: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _ = responder.RunAsync(cts.Token);
        var answered = 0;
        responder.Answered += () => Interlocked.Increment(ref answered);

        using var attacker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        for (var i = 0; i < 2000; i++)
            await attacker.SendAsync(DiscoveryWire.BuildProbe(pc.PublicKeyBlob, Nonce()), new IPEndPoint(IPAddress.Loopback, responder.Port));
        await Task.Delay(500);
        Assert.True(answered <= DiscoveryResponder.MaxRepliesPerSecond * 2, $"answered {answered}");

        await Task.Delay(1100); // bucket refills
        using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var nonce = Nonce();
        await phone.SendAsync(DiscoveryWire.BuildProbe(pc.PublicKeyBlob, nonce), new IPEndPoint(IPAddress.Loopback, responder.Port));
        UdpReceiveResult reply;
        do { reply = await phone.ReceiveAsync(cts.Token); }
        while (DiscoveryWire.VerifyReply(reply.Buffer, nonce, pc.DeviceId, pc.PublicKeyBlob) is null);
    }

    [Fact]
    public void Reply_Is_Not_Larger_Than_Needed_For_Amplification()
    {
        // A probe the PC answers must not be a cheap way to make it send much
        // more data at a spoofed victim.
        var pc = DeviceIdentity.Generate();
        using var responder = new DiscoveryResponder(pc, () => 47820, udpPort: 0);
        var probe = DiscoveryWire.BuildProbe(pc.PublicKeyBlob, Nonce());
        var reply = responder.HandleDatagram(probe)!;
        Assert.True(reply.Length < probe.Length * 4, $"probe {probe.Length} B, reply {reply.Length} B");
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
