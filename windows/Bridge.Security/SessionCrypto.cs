using System.Security.Cryptography;

namespace Bridge.Security;

/// <summary>
/// Ephemeral ECDH (P-256) key agreement + HKDF-SHA256 derivation of two
/// directional AES-256-GCM keys, mirrored exactly on the Android side
/// (network/CryptoSession.kt) so a Windows and Android instance can
/// interoperate.
/// </summary>
public sealed class SessionCrypto : IDisposable
{
    private readonly ECDiffieHellman _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    public byte[] EphemeralPublicKey => _ephemeral.PublicKey.ExportSubjectPublicKeyInfo();

    private byte[]? _sendKey;
    private byte[]? _recvKey;

    /// <param name="peerEphemeralPublicKey">Peer's SubjectPublicKeyInfo bytes.</param>
    /// <param name="isInitiator">True for the side that sent HELLO first — determines which derived key is used to send vs receive.</param>
    public void CompleteHandshake(byte[] peerEphemeralPublicKey, bool isInitiator)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerEphemeralPublicKey, out _);

        byte[] sharedSecret = _ephemeral.DeriveKeyMaterial(peer.PublicKey);

        // Two directional keys via HKDF with distinct "info" labels.
        var toDevice = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, info: System.Text.Encoding.UTF8.GetBytes("bridge:win->android"));
        var toPc = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, info: System.Text.Encoding.UTF8.GetBytes("bridge:android->win"));

        _sendKey = isInitiator ? toDevice : toPc;
        _recvKey = isInitiator ? toPc : toDevice;
    }

    public byte[] Encrypt(byte[] plaintext)
    {
        if (_sendKey is null) throw new InvalidOperationException("Handshake not complete");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_sendKey, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return [.. nonce, .. ciphertext, .. tag];
    }

    public byte[] Decrypt(byte[] framed)
    {
        if (_recvKey is null) throw new InvalidOperationException("Handshake not complete");
        var nonce = framed[..12];
        var tag = framed[^16..];
        var ciphertext = framed[12..^16];
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_recvKey, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public void Dispose() => _ephemeral.Dispose();
}
