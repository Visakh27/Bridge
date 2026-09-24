using System.Text;
using Bridge.Protocol;
using Bridge.Security;

namespace Bridge.Network;

/// <summary>
/// Wraps an ITransport with the ephemeral-ECDH handshake + AES-GCM framing
/// described in protocol/protocol.md, and exposes send/receive at the level
/// of BridgeMessage rather than raw bytes.
/// </summary>
public sealed class EncryptedChannel : IAsyncDisposable
{
    private readonly ITransport _transport;
    private readonly SessionCrypto _crypto;

    private EncryptedChannel(ITransport transport, SessionCrypto crypto)
    {
        _transport = transport;
        _crypto = crypto;
    }

    public static async Task<EncryptedChannel> HandshakeAsInitiatorAsync(ITransport transport, string deviceId, CancellationToken ct = default)
    {
        var crypto = new SessionCrypto();
        await transport.SendFrameAsync(BuildHello(deviceId, crypto.EphemeralPublicKey), ct);
        var ack = await transport.ReceiveFrameAsync(ct);
        var (_, peerPub) = ParseHello(ack);
        crypto.CompleteHandshake(peerPub, isInitiator: true);
        return new EncryptedChannel(transport, crypto);
    }

    public static async Task<EncryptedChannel> HandshakeAsResponderAsync(ITransport transport, string deviceId, CancellationToken ct = default)
    {
        var crypto = new SessionCrypto();
        var hello = await transport.ReceiveFrameAsync(ct);
        var (_, peerPub) = ParseHello(hello);
        await transport.SendFrameAsync(BuildHello(deviceId, crypto.EphemeralPublicKey), ct);
        crypto.CompleteHandshake(peerPub, isInitiator: false);
        return new EncryptedChannel(transport, crypto);
    }

    private static byte[] BuildHello(string deviceId, byte[] ephemeralPub)
    {
        var idBytes = Encoding.UTF8.GetBytes(deviceId);
        var header = BitConverter.GetBytes(idBytes.Length);
        return [.. header, .. idBytes, .. ephemeralPub];
    }

    private static (string deviceId, byte[] pub) ParseHello(byte[] frame)
    {
        var idLen = BitConverter.ToInt32(frame, 0);
        var deviceId = Encoding.UTF8.GetString(frame, 4, idLen);
        var pub = frame[(4 + idLen)..];
        return (deviceId, pub);
    }

    public async Task SendAsync(BridgeMessage message, CancellationToken ct = default)
    {
        var plaintext = Encoding.UTF8.GetBytes(message.ToJson());
        var encrypted = _crypto.Encrypt(plaintext);
        await _transport.SendFrameAsync(encrypted, ct);
    }

    public async Task<BridgeMessage> ReceiveAsync(CancellationToken ct = default)
    {
        var frame = await _transport.ReceiveFrameAsync(ct);
        var plaintext = _crypto.Decrypt(frame);
        return BridgeMessage.FromJson(Encoding.UTF8.GetString(plaintext));
    }

    public bool IsConnected => _transport.IsConnected;

    public async ValueTask DisposeAsync()
    {
        _crypto.Dispose();
        await _transport.DisposeAsync();
    }
}
