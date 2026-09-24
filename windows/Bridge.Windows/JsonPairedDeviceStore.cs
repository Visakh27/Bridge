using System.Text.Json;
using Bridge.Network;

namespace Bridge.Windows;

/// <summary>Simple JSON-file-backed IPairedDeviceStore (SQLite is the recommended production store per section 5/14).</summary>
public sealed class JsonPairedDeviceStore : IPairedDeviceStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bridge", "paired_devices.json");

    public async Task SaveAsync(PairedDevice device)
    {
        var list = (await ListAsync()).Where(d => d.DeviceId != device.DeviceId).Append(device).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(list));
    }

    public async Task<IReadOnlyList<PairedDevice>> ListAsync()
    {
        if (!File.Exists(_path)) return [];
        var json = await File.ReadAllTextAsync(_path);
        return JsonSerializer.Deserialize<List<PairedDevice>>(json) ?? [];
    }

    public async Task RevokeAsync(string deviceId)
    {
        var list = (await ListAsync()).Where(d => d.DeviceId != deviceId).ToList();
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(list));
    }
}
