using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

public class CryptoTests
{
    [Fact]
    public void SessionCrypto_EncryptDecrypt_RoundTrips_BothDirections()
    {
        using var initiator = new SessionCrypto();
        using var responder = new SessionCrypto();

        initiator.CompleteHandshake(responder.EphemeralPublicKey, isInitiator: true);
        responder.CompleteHandshake(initiator.EphemeralPublicKey, isInitiator: false);

        var plaintext = System.Text.Encoding.UTF8.GetBytes("bridge session payload");
        var encrypted = initiator.Encrypt(plaintext);
        var decrypted = responder.Decrypt(encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void FileHasher_ProducesConsistentHash()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("bridge file contents");
        var hash1 = FileHasher.Sha256Hex(data);
        var hash2 = FileHasher.Sha256Hex(data);
        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // hex SHA-256
    }
}
