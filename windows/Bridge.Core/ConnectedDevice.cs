namespace Bridge.Core;

public sealed class ConnectedDevice
{
    private long _lastSeenTicks = DateTimeOffset.UtcNow.UtcTicks;

    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public int BatteryPercent { get; set; } = -1;
    public bool Charging { get; set; }
    public string ConnectionQuality { get; set; } = "unknown";
    public bool IsConnected { get; set; }
    public IReadOnlyCollection<string> PeerFeatures { get; set; } = [];

    public DateTimeOffset LastSeen
    {
        get => new(Interlocked.Read(ref _lastSeenTicks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _lastSeenTicks, value.UtcTicks);
    }
}
