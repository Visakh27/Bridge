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
    public const string NotificationPost = "notification.post";
    public const string NotificationDismiss = "notification.dismiss";
    public const string Error = "error";
}

public sealed class BridgeMessage
{
    public int Protocol { get; init; } = 1;
    public required string Type { get; init; }
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public required JsonObject Payload { get; init; }

    public static BridgeMessage Create(string type, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload)!.AsObject();
        return new BridgeMessage { Type = type, Payload = node };
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts.Options);

    public static BridgeMessage FromJson(string json) =>
        JsonSerializer.Deserialize<BridgeMessage>(json, JsonOpts.Options)
        ?? throw new InvalidDataException("Malformed Bridge message");

    public T PayloadAs<T>() => Payload.Deserialize<T>(JsonOpts.Options)!;
}

internal static class JsonOpts
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
