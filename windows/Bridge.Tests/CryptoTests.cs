using System.Security.Cryptography;
using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

public class CryptoTests
{
    private static (SessionCrypto Initiator, SessionCrypto Responder) Pair()
    {
        var initiator = new SessionCrypto();
        var responder = new SessionCrypto();
        var transcript = SessionCrypto.Transcript("phone", initiator.EphemeralPublicKey, "pc", responder.EphemeralPublicKey);
        initiator.CompleteHandshake(responder.EphemeralPublicKey, transcript, isInitiator: true);
        responder.CompleteHandshake(initiator.EphemeralPublicKey, transcript, isInitiator: false);
        return (initiator, responder);
    }

    [Fact]
    public void SessionCrypto_EncryptDecrypt_RoundTrips_BothDirections()
    {
        var (initiator, responder) = Pair();
        using (initiator) using (responder)
        {
            var plaintext = System.Text.Encoding.UTF8.GetBytes("bridge session payload");
            Assert.Equal(plaintext, responder.Decrypt(initiator.Encrypt(plaintext)));
            Assert.Equal(plaintext, initiator.Decrypt(responder.Encrypt(plaintext)));
        }
    }

    [Fact]
    public void SessionCrypto_Directions_UseDifferentKeys()
    {
        var (initiator, responder) = Pair();
        using (initiator) using (responder)
        {
            var frame = initiator.Encrypt([1, 2, 3]);
            // A frame reflected back to its sender must not decrypt.
            Assert.Throws<AuthenticationTagMismatchException>(() => initiator.Decrypt(frame));
        }
    }

    [Fact]
    public void SessionCrypto_RejectsReplayedAndReorderedFrames()
    {
        var (initiator, responder) = Pair();
        using (initiator) using (responder)
        {
            var first = initiator.Encrypt([1]);
            var second = initiator.Encrypt([2]);
            Assert.Throws<CryptographicException>(() => responder.Decrypt(second)); // out of order
            responder.Decrypt(first);
            Assert.Throws<CryptographicException>(() => responder.Decrypt(first)); // replay
            Assert.Equal(new byte[] { 2 }, responder.Decrypt(second));
        }
    }

    [Fact]
    public void SessionCrypto_RejectsTamperedFrame()
    {
        var (initiator, responder) = Pair();
        using (initiator) using (responder)
        {
            var frame = initiator.Encrypt([9, 9, 9, 9]);
            frame[SessionCrypto.NonceSize] ^= 0x01;
            Assert.Throws<AuthenticationTagMismatchException>(() => responder.Decrypt(frame));
        }
    }

    [Fact]
    public void SessionCrypto_DifferentTranscripts_GiveDifferentKeys()
    {
        using var a = new SessionCrypto();
        using var b = new SessionCrypto();
        a.CompleteHandshake(b.EphemeralPublicKey, SessionCrypto.Transcript("x", a.EphemeralPublicKey, "y", b.EphemeralPublicKey), true);
        b.CompleteHandshake(a.EphemeralPublicKey, SessionCrypto.Transcript("x", a.EphemeralPublicKey, "z", b.EphemeralPublicKey), false);
        Assert.Throws<AuthenticationTagMismatchException>(() => b.Decrypt(a.Encrypt([1])));
    }

    [Fact]
    public void DeviceIdentity_SignAndVerify()
    {
        var identity = DeviceIdentity.Generate();
        var other = DeviceIdentity.Generate();
        var data = new byte[] { 1, 2, 3 };
        var sig = identity.Sign(data);

        Assert.Equal(0x30, sig[0]); // DER SEQUENCE, the format Java's SHA256withECDSA uses
        Assert.True(DeviceIdentity.Verify(identity.PublicKeyBlob, data, sig));
        Assert.False(DeviceIdentity.Verify(other.PublicKeyBlob, data, sig));
        Assert.False(DeviceIdentity.Verify(identity.PublicKeyBlob, [1, 2, 4], sig));
        Assert.False(DeviceIdentity.Verify([1, 2, 3], data, sig)); // garbage key never throws
    }

    [Fact]
    public void FileHasher_ProducesConsistentHash()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("bridge file contents");
        var hash1 = FileHasher.Sha256Hex(data);
        Assert.Equal(hash1, FileHasher.Sha256Hex(data));
        Assert.Equal(64, hash1.Length); // hex SHA-256
        // Known-answer test, and the same lowercase hex the Android side produces.
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", FileHasher.Sha256Hex("abc"u8));
    }
}
