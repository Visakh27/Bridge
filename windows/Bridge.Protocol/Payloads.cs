namespace Bridge.Protocol;

public sealed record CapabilitiesPayload(string[] Features);

public sealed record DeviceStatusPayload(int BatteryPercent, bool Charging, string ConnectionQuality);

public sealed record ClipboardUpdatePayload(string Format, string Data);

public sealed record FileStartPayload(string TransferId, string FileName, long Size, string Sha256, int TotalChunks);

public sealed record FileChunkPayload(string TransferId, int Index, string Data);

public sealed record FileEndPayload(string TransferId);

public sealed record FileAckPayload(string TransferId, string Status); // ok | hash_mismatch | cancelled

public sealed record FileCancelPayload(string TransferId);

public sealed record NotificationPostPayload(string AppName, string Title, string Text, string Key);

public sealed record NotificationDismissPayload(string Key);

public sealed record ErrorPayload(string Code, string Message);

public sealed record PairingRequestPayload(string PairingToken, string DeviceName, string PublicKeyBase64);

public sealed record PairingAcceptPayload(string DeviceName, string PublicKeyBase64);

public sealed record PairingRejectPayload(string Reason);

/// <summary>
/// Contents of the pairing QR code. <see cref="PublicKeyBase64"/> is the PC's
/// long-term identity key (SubjectPublicKeyInfo); the phone uses it to check
/// the PC's handshake signature before revealing the pairing token.
/// </summary>
public sealed record PairingQrPayload(string PairingToken, string Host, int Port, string DeviceId, string DeviceName, string PublicKeyBase64);

public static class FileAckStatus
{
    public const string Ok = "ok";
    public const string HashMismatch = "hash_mismatch";
    public const string Cancelled = "cancelled";
}
