namespace Bridge.Core;

public sealed class ConnectedDevice
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public int BatteryPercent { get; set; } = -1;
    public bool Charging { get; set; }
    public string ConnectionQuality { get; set; } = "unknown";
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    public bool IsConnected { get; set; }
}
