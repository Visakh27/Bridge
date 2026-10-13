namespace Bridge.Protocol;

public sealed record CapabilitiesPayload(string[] Features);

public sealed record DeviceStatusPayload(int BatteryPercent, bool Charging, string ConnectionQuality);

public sealed record ClipboardUpdatePayload(string Format, string Data);

/// <summary>
/// Announces a file. Optional fields (null = absent on the wire):
/// <see cref="Resumable"/> — the sender waits for <c>file.resume</c> before sending chunks (peer advertised <c>files.resume</c>);
/// <see cref="RelativePath"/> — folder path of this file inside a folder transfer, '/'-separated, without the file name;
/// <see cref="BatchId"/>/<see cref="BatchIndex"/>/<see cref="BatchCount"/> — groups the files of one folder transfer.
/// </summary>
public sealed record FileStartPayload(string TransferId, string FileName, long Size, string Sha256, int TotalChunks)
{
    public bool? Resumable { get; init; }
    public string? RelativePath { get; init; }
    public string? BatchId { get; init; }
    public int? BatchIndex { get; init; }
    public int? BatchCount { get; init; }
}

/// <summary>Receiver → sender: continue from chunk <see cref="NextIndex"/> (0 = from the start).</summary>
public sealed record FileResumePayload(string TransferId, int NextIndex);

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
