using System.Text;
using System.Text.Json;
using Bridge.Protocol;
using Bridge.Security;
using QRCoder;

namespace Bridge.Network;

public sealed record PairingQrPayload(string PairingToken, string Host, int Port, string DeviceId, string DeviceName);

public sealed record PairedDevice(string DeviceId, string DeviceName, byte[] LongTermPublicKey);

/// <summary>
/// Windows side of pairing (section 6 of the blueprint): generates a
/// short-lived single-use token, renders it as a QR code, accepts the
/// resulting connection, and persists the paired device on success.
/// </summary>
public sealed class PairingService
{
    private readonly DeviceIdentity _identity;
    private readonly IPairedDeviceStore _store;
    private string? _activeToken;

    public PairingService(DeviceIdentity identity, IPairedDeviceStore store)
    {
        _identity = identity;
        _store = store;
    }

    public (string PngDataUri, PairingQrPayload Payload) GenerateQrCode(int listenPort, string deviceName)
    {
        _activeToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var host = LocalNetwork.GetLikelyLanIPv4();
        var payload = new PairingQrPayload(_activeToken, host, listenPort, _identity.DeviceId, deviceName);
        var json = JsonSerializer.Serialize(payload);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(json, QRCodeGenerator.ECCLevel.M);
        using var pngQr = new PngByteQRCode(data);
        var bytes = pngQr.GetGraphic(10);
        return ($"data:image/png;base64,{Convert.ToBase64String(bytes)}", payload);
    }

    /// <summary>Called once an EncryptedChannel is up and the peer sends pairing.request.</summary>
    public async Task<PairedDevice?> HandlePairingRequestAsync(EncryptedChannel channel, BridgeMessage request, CancellationToken ct = default)
    {
        var payload = request.PayloadAs<PairingRequestPayload>();

        if (_activeToken is null || payload.PairingToken != _activeToken)
        {
            await channel.SendAsync(BridgeMessage.Create(MessageType.PairingReject,
                new PairingRejectPayload("Invalid or expired pairing token")), ct);
            return null;
        }

        _activeToken = null; // single-use

        var peerKey = Convert.FromBase64String(payload.PublicKeyBase64);
        var device = new PairedDevice(request.Id, payload.DeviceName, peerKey);
        await _store.SaveAsync(device);

        await channel.SendAsync(BridgeMessage.Create(MessageType.PairingAccept,
            new PairingAcceptPayload("This PC", Convert.ToBase64String(_identity.PublicKeyBlob))), ct);

        return device;
    }
}

public interface IPairedDeviceStore
{
    Task SaveAsync(PairedDevice device);
    Task<IReadOnlyList<PairedDevice>> ListAsync();
    Task RevokeAsync(string deviceId);
}
