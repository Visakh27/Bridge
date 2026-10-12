using System.Text;

namespace Bridge.Network;

/// <summary>
/// Turns a peer-supplied file name into a safe leaf name inside the download
/// folder. The name in file.start is attacker-controlled: without this,
/// "..\\..\\AppData\\…\\Startup\\x.exe" or an absolute path would be written
/// outside the download folder.
/// </summary>
public static class SafeFileName
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const int MaxLength = 200;

    public static string Sanitize(string? name)
    {
        // Take the last path segment for either separator, whatever OS we run on.
        var leaf = (name ?? string.Empty).Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];

        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*']).ToHashSet();
        var sb = new StringBuilder(leaf.Length);
        foreach (var c in leaf) sb.Append(char.IsControl(c) || invalid.Contains(c) ? '_' : c);

        // Windows ignores trailing dots/spaces, which can be used to alias names.
        var cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned.All(c => c == '.')) cleaned = "received_file";

        var stem = cleaned.Split('.')[0];
        if (ReservedWindowsNames.Contains(stem)) cleaned = "_" + cleaned;

        if (cleaned.Length > MaxLength)
        {
            var ext = Path.GetExtension(cleaned);
            if (ext.Length > 20) ext = string.Empty;
            cleaned = cleaned[..(MaxLength - ext.Length)] + ext;
        }
        return cleaned;
    }

    /// <summary>A path in <paramref name="directory"/> that doesn't exist yet: "name.ext", then "name (1).ext", …</summary>
    public static string UniquePath(string directory, string safeName)
    {
        var candidate = Path.Combine(directory, safeName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(safeName);
        var ext = Path.GetExtension(safeName);
        for (var i = 1; ; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
