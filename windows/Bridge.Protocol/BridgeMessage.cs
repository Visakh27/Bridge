using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bridge.Protocol;

/// <summary>
/// Message types defined by protocol/protocol.md. Kept as constants (not an
/// enum) so unrecognized types from a newer peer deserialize without throwing.
/// </summary>
public static class MessageType
{
    public const string PairingRequest = "pairing.request";
    public const string PairingAccept = "pairing.accept";
    public const string PairingReject = "pairing.reject";
    public const string CapabilitiesAdvertise = "capabilities.advertise";
    public const string PresencePing = "presence.ping";
    public const string PresencePong = "presence.pong";
    public const string DeviceStatus = "device.status";
    public const string ClipboardUpdate = "clipboard.update";
    public const string FileStart = "file.start";
    public const string FileChunk = "file.chunk";
    public const string FileEnd = "file.end";
    public const string FileAck = "file.ack";
    public const string FileCancel = "file.cancel";
    public const string FileResume = "file.resume";
    public const string NotificationPost = "notification.post";
    public const string NotificationDismiss = "notification.dismiss";
    public const string NotificationAction = "notification.action";
    public const string NotificationActionResult = "notification.action.result";
    public const string NotificationMute = "notification.mute";
    public const string Error = "error";
}

/// <summary>Feature names carried in capabilities.advertise.</summary>
public static class Features
{
    public const string Files = "files";
    public const string Clipboard = "clipboard";
    public const string Notifications = "notifications";
    public const string Photos = "photos";
    /// <summary>Interrupted transfers continue where they stopped (file.resume).</summary>
    public const string FilesResume = "files.resume";
    /// <summary>Folder transfers (file.start relativePath / batch fields).</summary>
    public const string FilesFolders = "files.folders";
    /// <summary>Reply to, press buttons on, dismiss and mute phone notifications from the PC.</summary>
    public const string NotificationsActions = "notifications.actions";
}

/// <summary>Error codes carried in error messages (docs/architecture.md).</summary>
public static class ErrorCodes
{
    public const string ConnectionTimeout = "CONNECTION_TIMEOUT";
    public const string AuthFailed = "AUTH_FAILED";
    public const string DeviceUnpaired = "DEVICE_UNPAIRED";
    public const string TransferInterrupted = "TRANSFER_INTERRUPTED";
    public const string PermissionDenied = "PERMISSION_DENIED";
}

public sealed class BridgeMessage
{
    public const int CurrentProtocol = 1;

    public int Protocol { get; init; } = CurrentProtocol;
    public required string Type { get; init; }
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public required JsonObject Payload { get; init; }

    /// <summary>
    /// Builds a message whose payload is serialised with the wire naming policy
    /// (camelCase). Serialising with default options would emit PascalCase keys,
    /// which the Android peer rejects.
    /// </summary>
    public static BridgeMessage Create(string type, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, payload.GetType(), BridgeJson.Options) as JsonObject
            ?? throw new ArgumentException("Payload must serialise to a JSON object", nameof(payload));
        return new BridgeMessage { Type = type, Payload = node };
    }

    /// <summary>A message with an empty <c>{}</c> payload (presence.ping / presence.pong).</summary>
    public static BridgeMessage CreateEmpty(string type) => new() { Type = type, Payload = new JsonObject() };

    public string ToJson() => JsonSerializer.Serialize(this, BridgeJson.Options);

    /// <exception cref="InvalidDataException">Malformed JSON or missing required fields.</exception>
    public static BridgeMessage FromJson(string json)
    {
        BridgeMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<BridgeMessage>(json, BridgeJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed Bridge message", ex);
        }
        if (message is null) throw new InvalidDataException("Malformed Bridge message");
        if (message.Payload is null || string.IsNullOrEmpty(message.Type))
            throw new InvalidDataException("Bridge message is missing type or payload");
        return message;
    }

    /// <exception cref="InvalidDataException">Payload doesn't match <typeparamref name="T"/>.</exception>
    public T PayloadAs<T>()
    {
        try
        {
            return Payload.Deserialize<T>(BridgeJson.Options) ?? throw new InvalidDataException($"Malformed {Type} payload");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Malformed {Type} payload", ex);
        }
    }
}

/// <summary>
/// The one JSON configuration for everything that goes on the wire (messages,
/// payloads and the pairing QR code), so both platforms see camelCase keys.
/// </summary>
public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // Optional fields that are null are left off the wire entirely.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Wire JSON is never embedded in HTML, so don't escape '+', '<', etc.
        // (the default turns every '+' in base64 into "\u002B", bloating
        // frames, discovery datagrams and the pairing QR code).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
