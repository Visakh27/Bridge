using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bridge.Security;

/// <summary>
/// Ephemeral ECDH (P-256) key agreement + HKDF-SHA256 derivation of two
/// directional AES-256-GCM keys, mirrored exactly on the Android side
/// (security/SessionCrypto.kt) so Windows and Android interoperate.
///
/// HKDF salt is the handshake transcript hash, which binds the session keys to
/// both device ids and both ephemeral keys. Nonces are a per-direction 64-bit
/// counter (4 zero bytes + big-endian counter), so the receiver rejects any
/// replayed, dropped or reordered frame.
/// </summary>
public sealed class SessionCrypto : IDisposable
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    private const string InitiatorToResponder = "bridge-v1 initiator->responder";
    private const string ResponderToInitiator = "bridge-v1 responder->initiator";
    private const string TranscriptLabel = "bridge-handshake-v1";

    private readonly ECDiffieHellman _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    public byte[] EphemeralPublicKey => _ephemeral.PublicKey.ExportSubjectPublicKeyInfo();

    private AesGcm? _send;
    private AesGcm? _recv;
    private ulong _sendCounter;
    private ulong _recvCounter;

    /// <summary>
    /// SHA-256 over the label and each field length-prefixed (4-byte big-endian),
    /// in the order initiator id, initiator key, responder id, responder key.
    /// Both peers sign this value with their long-term identity key.
    /// </summary>
    public static byte[] Transcript(string initiatorId, byte[] initiatorEphemeral, string responderId, byte[] responderEphemeral)
    {
        using var ms = new MemoryStream();
        void Field(byte[] value)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, value.Length);
            ms.Write(len);
            ms.Write(value);
        }
        Field(Encoding.UTF8.GetBytes(TranscriptLabel));
        Field(Encoding.UTF8.GetBytes(initiatorId));
        Field(initiatorEphemeral);
        Field(Encoding.UTF8.GetBytes(responderId));
        Field(responderEphemeral);
        return SHA256.HashData(ms.ToArray());
    }

    /// <param name="peerEphemeralPublicKey">Peer's SubjectPublicKeyInfo bytes.</param>
    /// <param name="transcript">Value from <see cref="Transcript"/>.</param>
    /// <param name="isInitiator">True for the side that sent HELLO (Android).</param>
    public void CompleteHandshake(byte[] peerEphemeralPublicKey, byte[] transcript, bool isInitiator)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerEphemeralPublicKey, out _);
        if (peer.KeySize != 256) throw new CryptographicException("Peer ephemeral key is not P-256");

        byte[] sharedSecret = _ephemeral.DeriveRawSecretAgreement(peer.PublicKey);
        try
        {
            var i2r = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, transcript, Encoding.UTF8.GetBytes(InitiatorToResponder));
            var r2i = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, transcript, Encoding.UTF8.GetBytes(ResponderToInitiator));
            _send = new AesGcm(isInitiator ? i2r : r2i, TagSize);
            _recv = new AesGcm(isInitiator ? r2i : i2r, TagSize);
            CryptographicOperations.ZeroMemory(i2r);
            CryptographicOperations.ZeroMemory(r2i);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    /// <summary>Returns nonce || ciphertext || tag. Callers must serialise Encrypt + send so frames hit the wire in counter order.</summary>
    public byte[] Encrypt(byte[] plaintext)
    {
        if (_send is null) throw new InvalidOperationException("Handshake not complete");
        var output = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _sendCounter++);
        _send.Encrypt(nonce, plaintext, output.AsSpan(NonceSize, plaintext.Length), output.AsSpan(NonceSize + plaintext.Length));
        return output;
    }

    public byte[] Decrypt(byte[] framed)
    {
        if (_recv is null) throw new InvalidOperationException("Handshake not complete");
        if (framed.Length < NonceSize + TagSize) throw new CryptographicException("Frame too short");

        var nonce = framed.AsSpan(0, NonceSize);
        if (BinaryPrimitives.ReadUInt32BigEndian(nonce) != 0 || BinaryPrimitives.ReadUInt64BigEndian(nonce[4..]) != _recvCounter)
            throw new CryptographicException("Unexpected nonce (replayed, dropped or reordered frame)");

        var ctLen = framed.Length - NonceSize - TagSize;
        var plaintext = new byte[ctLen];
        _recv.Decrypt(nonce, framed.AsSpan(NonceSize, ctLen), framed.AsSpan(NonceSize + ctLen), plaintext);
        _recvCounter++;
        return plaintext;
    }

    public void Dispose()
    {
        _ephemeral.Dispose();
        _send?.Dispose();
        _recv?.Dispose();
    }
}
