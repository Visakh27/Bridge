using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

public class ChannelAndPairingTests
{
    [Fact]
    public async Task Handshake_AuthenticatesBothSides()
    {
        var phone = DeviceIdentity.Generate();
        var pc = DeviceIdentity.Generate();
        var (client, server) = await Loopback.ConnectAsync();

        var responder = EncryptedChannel.HandshakeAsResponderAsync(server, pc);
        await using var initiator = await EncryptedChannel.HandshakeAsInitiatorAsync(client, phone, pc.PublicKeyBlob).WithTimeout();
        await using var resp = await responder.WithTimeout();

        Assert.Equal(pc.DeviceId, initiator.PeerDeviceId);
        Assert.Equal(phone.DeviceId, resp.PeerDeviceId);
        Assert.True(resp.VerifyPeer(phone.PublicKeyBlob));
        Assert.False(resp.VerifyPeer(DeviceIdentity.Generate().PublicKeyBlob));

        await initiator.SendAsync(BridgeMessage.CreateEmpty(MessageType.PresencePing));
        Assert.Equal(MessageType.PresencePing, (await resp.ReceiveAsync().WithTimeout()).Type);
        await resp.SendAsync(BridgeMessage.CreateEmpty(MessageType.PresencePong));
        Assert.Equal(MessageType.PresencePong, (await initiator.ReceiveAsync().WithTimeout()).Type);
    }

    [Fact]
    public async Task Handshake_Initiator_Refuses_Impostor_Responder()
    {
        // A LAN attacker answering instead of the real PC can't sign as the PC.
        var phone = DeviceIdentity.Generate();
        var realPc = DeviceIdentity.Generate();
        var impostor = DeviceIdentity.Generate();
        var (client, server) = await Loopback.ConnectAsync();

        var responder = EncryptedChannel.HandshakeAsResponderAsync(server, impostor);
        await Assert.ThrowsAnyAsync<AuthenticationException>(() => EncryptedChannel.HandshakeAsInitiatorAsync(client, phone, realPc.PublicKeyBlob));
        await client.DisposeAsync();
        try { await (await responder).DisposeAsync(); } catch { /* expected: initiator hung up */ }
    }

    [Fact]
    public async Task Pairing_Accepts_Valid_Token_And_Stores_Handshake_DeviceId()
    {
        var phone = DeviceIdentity.Generate();
        var pc = DeviceIdentity.Generate();
        var store = new InMemoryPairedDeviceStore();
        var pairing = new PairingService(pc, store, "Office PC");
        var qr = pairing.CreateQrPayload(47820, "127.0.0.1");
        Assert.Equal(Convert.ToBase64String(pc.PublicKeyBlob), qr.PublicKeyBase64);

        var (client, server) = await Loopback.ConnectAsync();
        var respTask = EncryptedChannel.HandshakeAsResponderAsync(server, pc);
        await using var init = await EncryptedChannel.HandshakeAsInitiatorAsync(client, phone, Convert.FromBase64String(qr.PublicKeyBase64));
        await using var resp = await respTask;

        var request = BridgeMessage.Create(MessageType.PairingRequest,
            new PairingRequestPayload(qr.PairingToken, "Pixel 8", Convert.ToBase64String(phone.PublicKeyBlob)));
        var paired = await pairing.HandlePairingRequestAsync(resp, request);

        Assert.NotNull(paired);
        Assert.Equal(phone.DeviceId, paired!.DeviceId); // not the message id
        Assert.Equal(phone.PublicKeyBlob, paired.LongTermPublicKey);
        var reply = await init.ReceiveAsync().WithTimeout();
        Assert.Equal(MessageType.PairingAccept, reply.Type);
        Assert.Equal("Office PC", reply.PayloadAs<PairingAcceptPayload>().DeviceName);

        // Token is single-use.
        var again = await pairing.HandlePairingRequestAsync(resp, request);
        Assert.Null(again);
    }

    [Fact]
    public async Task Pairing_Rejects_Wrong_Token_Expired_Token_And_Foreign_Key()
    {
        var phone = DeviceIdentity.Generate();
        var pc = DeviceIdentity.Generate();
        var store = new InMemoryPairedDeviceStore();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var pairing = new PairingService(pc, store, "PC", clock);
        var qr = pairing.CreateQrPayload(1, "127.0.0.1");

        var (client, server) = await Loopback.ConnectAsync();
        var respTask = EncryptedChannel.HandshakeAsResponderAsync(server, pc);
        await using var init = await EncryptedChannel.HandshakeAsInitiatorAsync(client, phone, pc.PublicKeyBlob);
        await using var resp = await respTask;

        async Task<string> Attempt(string token, byte[] key)
        {
            var result = await pairing.HandlePairingRequestAsync(resp, BridgeMessage.Create(MessageType.PairingRequest,
                new PairingRequestPayload(token, "x", Convert.ToBase64String(key))));
            Assert.Null(result);
            var reply = await init.ReceiveAsync().WithTimeout();
            Assert.Equal(MessageType.PairingReject, reply.Type);
            return reply.PayloadAs<PairingRejectPayload>().Reason;
        }

        Assert.Contains("Invalid", await Attempt("wrong", phone.PublicKeyBlob));
        // Someone else's key: the phone can't prove it owns it.
        Assert.Contains("verified", await Attempt(qr.PairingToken, DeviceIdentity.Generate().PublicKeyBlob));
        clock.Now += PairingService.DefaultTokenLifetime + TimeSpan.FromSeconds(1);
        Assert.Contains("expired", await Attempt(qr.PairingToken, phone.PublicKeyBlob));
        Assert.Equal(0, (await store.ListAsync()).Count);
    }

    [Fact]
    public void SafeFileName_Blocks_Traversal_And_Reserved_Names()
    {
        Assert.Equal("evil.exe", SafeFileName.Sanitize(@"..\..\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\evil.exe"));
        Assert.Equal("passwd", SafeFileName.Sanitize("../../../etc/passwd"));
        Assert.Equal("x.txt", SafeFileName.Sanitize(@"C:\Windows\x.txt"));
        Assert.Equal("_CON.txt", SafeFileName.Sanitize("CON.txt"));
        Assert.Equal("received_file", SafeFileName.Sanitize(".."));
        Assert.Equal("received_file", SafeFileName.Sanitize(""));
        Assert.Equal("a_b.txt", SafeFileName.Sanitize("a:b.txt"));
        Assert.Equal("photo.jpg", SafeFileName.Sanitize("photo.jpg"));
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
