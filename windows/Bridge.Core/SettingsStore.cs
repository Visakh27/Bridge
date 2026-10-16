using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bridge.Core;

/// <summary>Windows app settings (Settings panel). Defaults match earlier versions.</summary>
public sealed record BridgeSettings
{
    /// <summary>Where received files go; null = Downloads\Bridge.</summary>
    public string? DownloadFolder { get; init; }
    /// <summary>Start Bridge when you sign in to Windows (in the tray).</summary>
    public bool StartWithWindows { get; init; }
    /// <summary>Open to the tray instead of showing the window when Bridge starts.</summary>
    public bool StartMinimized { get; init; }
    /// <summary>Show the phone's notifications as Windows notifications.</summary>
    public bool ShowPhoneNotifications { get; init; } = true;
    /// <summary>Clipboard sync is opt-in and remembered.</summary>
    public bool ClipboardSync { get; init; }
    /// <summary>Keep a diagnostics log (metadata only).</summary>
    public bool Diagnostics { get; init; } = true;
    /// <summary>The first-run guide has been seen.</summary>
    public bool OnboardingDone { get; init; }
}

/// <summary>
/// Loads and saves <see cref="BridgeSettings"/> as JSON. A missing, unreadable
/// or corrupt file gives defaults; invalid values are corrected on load (a
/// download folder must be an absolute local path). Saves are atomic, so a
/// crash mid-write never loses the previous settings.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private BridgeSettings _current;

    public SettingsStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    public BridgeSettings Current { get { lock (_gate) return _current; } }

    /// <summary>Raised after a change is saved (with the new settings).</summary>
    public event Action<BridgeSettings>? Changed;

    /// <summary>Applies <paramref name="change"/>, validates and saves. Returns the stored settings.</summary>
    public BridgeSettings Update(Func<BridgeSettings, BridgeSettings> change)
    {
        BridgeSettings next;
        lock (_gate)
        {
            next = Validate(change(_current));
            if (next == _current) return next;
            _current = next;
            Save(next);
        }
        Changed?.Invoke(next);
        return next;
    }

    private static BridgeSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new BridgeSettings();
            return Validate(JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(path), Json) ?? new BridgeSettings());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new BridgeSettings();
        }
    }

    public static BridgeSettings Validate(BridgeSettings s) => s with { DownloadFolder = ValidFolder(s.DownloadFolder) };

    /// <summary>An absolute, local (not UNC/network, not relative) folder path, or null.</summary>
    public static string? ValidFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Length > 240) return null;
        if (folder.Any(char.IsControl) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        if (folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal)) return null;
        // Windows "C:\..." or (tests on Linux) "/...".
        var rooted = (folder.Length >= 3 && char.IsAsciiLetter(folder[0]) && folder[1] == ':' && folder[2] is '\\' or '/') ||
                     (Path.DirectorySeparatorChar == '/' && folder.StartsWith('/'));
        if (!rooted) return null;
        if (folder.Replace('\\', '/').Split('/').Any(seg => seg == "..")) return null;
        return folder;
    }

    private void Save(BridgeSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory for this run; the next save tries again.
        }
    }
}
