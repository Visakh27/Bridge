using System.Text.Json;
using Bridge.Network;

namespace Bridge.Windows;

/// <summary>
/// Simple JSON-file-backed IPairedDeviceStore (SQLite is the recommended
/// production store per section 5/14). Holds only device ids, names and
/// public keys. Writes are serialised and atomic (temp file + replace), so a
/// crash mid-write can't corrupt the pairing list.
/// </summary>
public sealed class JsonPairedDeviceStore : IPairedDeviceStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonPairedDeviceStore(string path) => _path = path;

    public async Task SaveAsync(PairedDevice device)
    {
        await _lock.WaitAsync();
        try
        {
            var list = (await ReadAsync()).Where(d => d.DeviceId != device.DeviceId).Append(device).ToList();
            await WriteAsync(list);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<PairedDevice>> ListAsync()
    {
        await _lock.WaitAsync();
        try { return await ReadAsync(); }
        finally { _lock.Release(); }
    }

    public async Task RevokeAsync(string deviceId)
    {
        await _lock.WaitAsync();
        try
        {
            var list = (await ReadAsync()).Where(d => d.DeviceId != deviceId).ToList();
            await WriteAsync(list);
        }
        finally { _lock.Release(); }
    }

    private async Task<List<PairedDevice>> ReadAsync()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var json = await File.ReadAllTextAsync(_path);
            return JsonSerializer.Deserialize<List<PairedDevice>>(json) ?? [];
        }
        catch (JsonException)
        {
            return []; // unreadable store: behave as "nothing paired" rather than crash
        }
    }

    private async Task WriteAsync(List<PairedDevice> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(list));
        File.Move(temp, _path, overwrite: true);
    }
}
