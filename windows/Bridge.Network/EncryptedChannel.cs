using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Bridge.Protocol;
using Bridge.Security;

namespace Bridge.Network;

/// <summary>
/// Wraps an ITransport with the authenticated ephemeral-ECDH handshake and
/// AES-GCM framing described in protocol/protocol.md, and exposes send/receive
/// at the level of BridgeMessage rather than raw bytes.
///
/// Handshake (raw frames, all lengths 4-byte big-endian):
///   HELLO     initiator → responder: [len][deviceId][len][ephemeralKey]
///   HELLO_ACK responder → initiator: [len][deviceId][len][ephemeralKey][len][signature]
///   AUTH      initiator → responder: [len][signature]
/// Signatures are ECDSA-P256-SHA256 (DER) by each side's long-term identity
/// key over <see cref="SessionCrypto.Transcript"/>.
/// </summary>
public sealed class EncryptedChannel : IAsyncDisposable
{
    private const int MaxIdLength = 256;
    private const int MaxKeyLength = 1024;
    private const int MaxSignatureLength = 256;

    private readonly ITransport _transport;
    private readonly SessionCrypto _crypto;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _transcript;
    private readonly byte[]? _peerSignature;
    private int _disposed;

    /// <summary>Device id the peer presented in its HELLO. Only trustworthy after <see cref="VerifyPeer"/> succeeds (responder) or the handshake completes (initiator).</summary>
    public string PeerDeviceId { get; }

    private EncryptedChannel(ITransport transport, SessionCrypto crypto, string peerDeviceId, byte[] transcript, byte[]? peerSignature)
    {
        _transport = transport;
        _crypto = crypto;
        PeerDeviceId = peerDeviceId;
        _transcript = transcript;
        _peerSignature = peerSignature;
    }

    /// <summary>
    /// Initiator side (the phone). Fails with <see cref="AuthenticationException"/>
    /// before anything secret is sent if the responder can't prove it holds the
    /// private key for <paramref name="expectedResponderKey"/>.
    /// </summary>
    public static async Task<EncryptedChannel> HandshakeAsInitiatorAsync(
        ITransport transport, DeviceIdentity identity, byte[] expectedResponderKey, CancellationToken ct = default)
    {
        var crypto = new SessionCrypto();
        try
        {
            var myEphemeral = crypto.EphemeralPublicKey;
            await transport.SendFrameAsync(Concat(Field(Encoding.UTF8.GetBytes(identity.DeviceId)), Field(myEphemeral)), ct);

            var reader = new FrameReader(await transport.ReceiveFrameAsync(ct));
            var peerId = Encoding.UTF8.GetString(reader.Next(MaxIdLength));
            var peerEphemeral = reader.Next(MaxKeyLength);
            var peerSig = reader.Next(MaxSignatureLength);
            reader.End();

            var transcript = SessionCrypto.Transcript(identity.DeviceId, myEphemeral, peerId, peerEphemeral);
            if (!DeviceIdentity.Verify(expectedResponderKey, transcript, peerSig))
                throw new AuthenticationException("Peer failed to prove its identity");

            await transport.SendFrameAsync(Field(identity.Sign(transcript)), ct);
            crypto.CompleteHandshake(peerEphemeral, transcript, isInitiator: true);
            return new EncryptedChannel(transport, crypto, peerId, transcript, null);
        }
        catch
        {
            crypto.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Responder side (the PC). The returned channel is encrypted but the peer is
    /// NOT yet authenticated: call <see cref="VerifyPeer"/> with the stored key of
    /// a paired device, or with the key from a pairing.request.
    /// </summary>
    public static async Task<EncryptedChannel> HandshakeAsResponderAsync(ITransport transport, DeviceIdentity identity, CancellationToken ct = default)
    {
        var crypto = new SessionCrypto();
        try
        {
            var hello = new FrameReader(await transport.ReceiveFrameAsync(ct));
            var peerId = Encoding.UTF8.GetString(hello.Next(MaxIdLength));
            var peerEphemeral = hello.Next(MaxKeyLength);
            hello.End();
            if (peerId.Length == 0) throw new InvalidDataException("Empty device id in HELLO");

            var myEphemeral = crypto.EphemeralPublicKey;
            var transcript = SessionCrypto.Transcript(peerId, peerEphemeral, identity.DeviceId, myEphemeral);
            await transport.SendFrameAsync(Concat(
                Field(Encoding.UTF8.GetBytes(identity.DeviceId)), Field(myEphemeral), Field(identity.Sign(transcript))), ct);

            var auth = new FrameReader(await transport.ReceiveFrameAsync(ct));
            var peerSig = auth.Next(MaxSignatureLength);
            auth.End();

            crypto.CompleteHandshake(peerEphemeral, transcript, isInitiator: false);
            return new EncryptedChannel(transport, crypto, peerId, transcript, peerSig);
        }
        catch
        {
            crypto.Dispose();
            throw;
        }
    }

    /// <summary>True if the initiator's handshake signature verifies under <paramref name="peerPublicKey"/> (SPKI).</summary>
    public bool VerifyPeer(byte[] peerPublicKey) =>
        _peerSignature is not null && DeviceIdentity.Verify(peerPublicKey, _transcript, _peerSignature);

    public async Task SendAsync(BridgeMessage message, CancellationToken ct = default)
    {
        var plaintext = Encoding.UTF8.GetBytes(message.ToJson());
        await _sendLock.WaitAsync(ct);
        try
        {
            // Encrypt inside the lock: nonces are sequence numbers, so frames
            // must reach the wire in the order they were encrypted.
            await _transport.SendFrameAsync(_crypto.Encrypt(plaintext), ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Single reader only. Throws on transport, integrity or JSON errors.</summary>
    public async Task<BridgeMessage> ReceiveAsync(CancellationToken ct = default)
    {
        var frame = await _transport.ReceiveFrameAsync(ct);
        var plaintext = _crypto.Decrypt(frame);
        return BridgeMessage.FromJson(Encoding.UTF8.GetString(plaintext));
    }

    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _transport.IsConnected;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _transport.DisposeAsync();
        _crypto.Dispose();
    }

    private static byte[] Field(byte[] value)
    {
        var buf = new byte[4 + value.Length];
        BinaryPrimitives.WriteInt32BigEndian(buf, value.Length);
        value.CopyTo(buf, 4);
        return buf;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private sealed class FrameReader(byte[] frame)
    {
        private int _offset;

        public byte[] Next(int maxLength)
        {
            if (frame.Length - _offset < 4) throw new InvalidDataException("Truncated handshake frame");
            var len = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(_offset));
            _offset += 4;
            if (len < 0 || len > maxLength || len > frame.Length - _offset)
                throw new InvalidDataException("Invalid field length in handshake frame");
            var value = frame.AsSpan(_offset, len).ToArray();
            _offset += len;
            return value;
        }

        public void End()
        {
            if (_offset != frame.Length) throw new InvalidDataException("Trailing bytes in handshake frame");
        }
    }
}

public sealed class AuthenticationException(string message) : CryptographicException(message);
