using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bridge.Protocol;
using Bridge.Security;
using QRCoder;

namespace Bridge.Network;

public sealed record PairedDevice(string DeviceId, string DeviceName, byte[] LongTermPublicKey);

/// <summary>
/// Windows side of pairing (section 6 of the blueprint): generates a
/// short-lived single-use token, renders it as a QR code together with this
/// PC's identity key, and on a pairing.request checks the token and that the
/// phone actually holds the key it presents before persisting the pairing.
/// </summary>
public sealed class PairingService
{
    public static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromMinutes(5);

    private readonly DeviceIdentity _identity;
    private readonly IPairedDeviceStore _store;
    private readonly string _deviceName;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private string? _activeToken;
    private DateTimeOffset _tokenExpiresAt;

    public PairingService(DeviceIdentity identity, IPairedDeviceStore store, string deviceName, TimeProvider? time = null)
    {
        _identity = identity;
        _store = store;
        _deviceName = deviceName;
        _time = time ?? TimeProvider.System;
    }

    public DateTimeOffset TokenExpiresAt { get { lock (_gate) return _tokenExpiresAt; } }

    /// <summary>Issues a fresh token (invalidating the previous one) and returns the QR payload.</summary>
    public PairingQrPayload CreateQrPayload(int listenPort, string? host = null, TimeSpan? lifetime = null)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        lock (_gate)
        {
            _activeToken = token;
            _tokenExpiresAt = _time.GetUtcNow() + (lifetime ?? DefaultTokenLifetime);
        }
        return new PairingQrPayload(token, host ?? LocalNetwork.GetLikelyLanIPv4(), listenPort,
            _identity.DeviceId, _deviceName, Convert.ToBase64String(_identity.PublicKeyBlob));
    }

    /// <summary>Same as <see cref="CreateQrPayload"/>, rendered as a PNG for display.</summary>
    public (byte[] Png, PairingQrPayload Payload) GenerateQrCode(int listenPort, string? host = null)
    {
        var payload = CreateQrPayload(listenPort, host);
        var json = JsonSerializer.Serialize(payload, BridgeJson.Options); // camelCase, as Android expects

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(json, QRCodeGenerator.ECCLevel.M);
        using var pngQr = new PngByteQRCode(data);
        return (pngQr.GetGraphic(10), payload);
    }

    /// <summary>
    /// Handles a pairing.request on a freshly handshaken channel. Replies with
    /// pairing.accept or pairing.reject; returns the stored device on success.
    /// </summary>
    public async Task<PairedDevice?> HandlePairingRequestAsync(EncryptedChannel channel, BridgeMessage request, CancellationToken ct = default)
    {
        var payload = request.PayloadAs<PairingRequestPayload>();

        string? rejection = null;
        byte[]? peerKey = null;
        lock (_gate)
        {
            if (_activeToken is null || _time.GetUtcNow() > _tokenExpiresAt)
                rejection = "Pairing code expired or already used. Generate a new code on the PC.";
            else if (!CryptographicOperations.FixedTimeEquals(
                         Encoding.UTF8.GetBytes(payload.PairingToken ?? string.Empty), Encoding.UTF8.GetBytes(_activeToken)))
                rejection = "Invalid pairing code";
        }

        if (rejection is null)
        {
            try { peerKey = Convert.FromBase64String(payload.PublicKeyBase64 ?? string.Empty); }
            catch (FormatException) { }
            // The phone signed the handshake transcript; the key it presents must
            // verify that signature, otherwise it doesn't own the key.
            if (peerKey is null || !channel.VerifyPeer(peerKey)) rejection = "Device identity could not be verified";
        }

        if (rejection is null)
        {
            lock (_gate)
            {
                if (_activeToken is null) rejection = "Pairing code already used";
                else _activeToken = null; // single-use
            }
        }

        if (rejection is not null)
        {
            await channel.SendAsync(BridgeMessage.Create(MessageType.PairingReject, new PairingRejectPayload(rejection)), ct);
            return null;
        }

        // Key the pairing by the device id from the handshake (not the message id).
        var deviceName = string.IsNullOrWhiteSpace(payload.DeviceName) ? "Android device" : payload.DeviceName.Trim();
        var device = new PairedDevice(channel.PeerDeviceId, deviceName, peerKey!);
        await _store.SaveAsync(device);

        await channel.SendAsync(BridgeMessage.Create(MessageType.PairingAccept,
            new PairingAcceptPayload(_deviceName, Convert.ToBase64String(_identity.PublicKeyBlob))), ct);
        return device;
    }
}

public interface IPairedDeviceStore
{
    Task SaveAsync(PairedDevice device);
    Task<IReadOnlyList<PairedDevice>> ListAsync();
    Task RevokeAsync(string deviceId);
}

/// <summary>In-memory store for tests and as a fallback.</summary>
public sealed class InMemoryPairedDeviceStore : IPairedDeviceStore
{
    private readonly List<PairedDevice> _devices = [];

    public Task SaveAsync(PairedDevice device)
    {
        lock (_devices) { _devices.RemoveAll(d => d.DeviceId == device.DeviceId); _devices.Add(device); }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PairedDevice>> ListAsync()
    {
        lock (_devices) return Task.FromResult<IReadOnlyList<PairedDevice>>(_devices.ToList());
    }

    public Task RevokeAsync(string deviceId)
    {
        lock (_devices) _devices.RemoveAll(d => d.DeviceId == deviceId);
        return Task.CompletedTask;
    }
}
