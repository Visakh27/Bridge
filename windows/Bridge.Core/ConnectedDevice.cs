namespace Bridge.Core;

public sealed class ConnectedDevice
{
    private long _lastSeenTicks = DateTimeOffset.UtcNow.UtcTicks;

    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    /// <summary>How this phone is connected: <see cref="ConnectionRoute.Lan"/> or <see cref="ConnectionRoute.Relay"/>.</summary>
    public string Route { get; init; } = ConnectionRoute.Lan;
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

public static class ConnectionRoute
{
    /// <summary>Directly, on the local network.</summary>
    public const string Lan = "lan";
    /// <summary>Through the user's relay (still end-to-end encrypted).</summary>
    public const string Relay = "relay";
}
