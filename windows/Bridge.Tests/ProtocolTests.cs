using System.Text.Json;
using System.Text.Json.Nodes;
using Bridge.Protocol;
using Xunit;

namespace Bridge.Tests;

public class ProtocolTests
{
    [Fact]
    public void RoundTrips_ClipboardMessage_ThroughJson()
    {
        var original = BridgeMessage.Create(MessageType.ClipboardUpdate, new ClipboardUpdatePayload("text", "hello bridge"));
        var parsed = BridgeMessage.FromJson(original.ToJson());
        var payload = parsed.PayloadAs<ClipboardUpdatePayload>();

        Assert.Equal(MessageType.ClipboardUpdate, parsed.Type);
        Assert.Equal(1, parsed.Protocol);
        Assert.Equal("hello bridge", payload.Data);
        Assert.Equal("text", payload.Format);
    }

    [Fact]
    public void FileStartPayload_RoundTrips()
    {
        var msg = BridgeMessage.Create(MessageType.FileStart, new FileStartPayload("t1", "photo.jpg", 12345, "abcd", 1));
        var parsed = BridgeMessage.FromJson(msg.ToJson()).PayloadAs<FileStartPayload>();

        Assert.Equal("photo.jpg", parsed.FileName);
        Assert.Equal(12345, parsed.Size);
        Assert.Equal(1, parsed.TotalChunks);
    }

    [Fact]
    public void Wire_Json_Uses_CamelCase_Keys_Everywhere()
    {
        // Android's kotlinx.serialization is case-sensitive: PascalCase keys are rejected.
        var json = BridgeMessage.Create(MessageType.FileStart, new FileStartPayload("t1", "a.txt", 1, "ab", 1)).ToJson();
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(new[] { "protocol", "type", "id", "timestamp", "payload" }, root.Select(p => p.Key).ToArray());
        Assert.Equal(new[] { "transferId", "fileName", "size", "sha256", "totalChunks" }, root["payload"]!.AsObject().Select(p => p.Key).ToArray());

        var qr = JsonSerializer.Serialize(new PairingQrPayload("t", "10.0.0.2", 47820, "id", "PC", "key"), BridgeJson.Options);
        Assert.Contains("\"pairingToken\"", qr);
        Assert.Contains("\"publicKeyBase64\"", qr);
        Assert.DoesNotContain("PairingToken", qr);
    }

    [Fact]
    public void Parses_Android_Style_Message()
    {
        // Shape produced by the Android client (nanosecond timestamp, explicit protocol field).
        const string json = """{"protocol":1,"type":"device.status","id":"8a3b2f1e-1b2c-4d5e-8f90-123456789abc","timestamp":"2026-10-12T07:44:39.741491401Z","payload":{"batteryPercent":81,"charging":true,"connectionQuality":"good"}}""";
        var msg = BridgeMessage.FromJson(json);
        var status = msg.PayloadAs<DeviceStatusPayload>();
        Assert.Equal(81, status.BatteryPercent);
        Assert.True(status.Charging);
        Assert.Equal(2026, msg.Timestamp.Year);
    }

    [Fact]
    public void Empty_Payload_Is_An_Object()
    {
        var json = BridgeMessage.CreateEmpty(MessageType.PresencePing).ToJson();
        Assert.Contains("\"payload\":{}", json);
    }

    [Fact]
    public void Rejects_Message_Without_Payload()
    {
        Assert.Throws<InvalidDataException>(() => BridgeMessage.FromJson("""{"protocol":1,"type":"presence.ping","id":"x","timestamp":"2026-10-12T00:00:00Z"}"""));
    }
}
