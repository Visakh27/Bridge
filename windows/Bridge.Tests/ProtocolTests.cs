using Bridge.Protocol;
using Xunit;

namespace Bridge.Tests;

public class ProtocolTests
{
    [Fact]
    public void RoundTrips_ClipboardMessage_ThroughJson()
    {
        var original = BridgeMessage.Create(MessageType.ClipboardUpdate, new ClipboardUpdatePayload("text", "hello bridge"));
        var json = original.ToJson();

        var parsed = BridgeMessage.FromJson(json);
        var payload = parsed.PayloadAs<ClipboardUpdatePayload>();

        Assert.Equal(MessageType.ClipboardUpdate, parsed.Type);
        Assert.Equal(1, parsed.Protocol);
        Assert.Equal("hello bridge", payload.Data);
        Assert.Equal("text", payload.Format);
    }

    [Fact]
    public void FileStartPayload_RoundTrips()
    {
        var msg = BridgeMessage.Create(MessageType.FileStart,
            new FileStartPayload("t1", "photo.jpg", 12345, "abcd", 1));
        var parsed = BridgeMessage.FromJson(msg.ToJson()).PayloadAs<FileStartPayload>();

        Assert.Equal("photo.jpg", parsed.FileName);
        Assert.Equal(12345, parsed.Size);
        Assert.Equal(1, parsed.TotalChunks);
    }
}
