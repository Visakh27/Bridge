namespace Bridge.Network;

/// <summary>
/// Maps a peer-supplied folder path (file.start relativePath, '/'-separated, e.g.
/// "Holiday/day 1") to a directory inside the download folder. Every segment is
/// sanitised like a file name, "." / ".." / empty segments are dropped, depth is
/// capped, and the result is checked to be inside the download folder.
/// </summary>
public static class SafeRelativePath
{
    public const int MaxDepth = 16;

    /// <summary>Sanitised segments, or null if the path is too deep.</summary>
    public static IReadOnlyList<string>? Segments(string? relativePath)
    {
        var parts = (relativePath ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p is not ("." or ".."))
            .Select(SafeFileName.Sanitize)
            .ToList();
        return parts.Count > MaxDepth ? null : parts;
    }

    /// <summary>
    /// The directory a file of a folder transfer goes in. <paramref name="rootName"/> is
    /// the (already de-duplicated) name chosen for the batch's top folder; the
    /// remaining segments of <paramref name="relativePath"/> are appended under it.
    /// </summary>
    public static string? Resolve(string downloadDir, string rootName, string? relativePath)
    {
        var segments = Segments(relativePath);
        if (segments is null) return null;
        var parts = new List<string> { downloadDir, rootName };
        parts.AddRange(segments.Skip(1)); // segment 0 is the batch's top folder, replaced by rootName
        var full = Path.GetFullPath(Path.Combine(parts.ToArray()));
        var root = Path.GetFullPath(downloadDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.Ordinal) ? full : null; // defence in depth
    }

    /// <summary>A top-level folder name in <paramref name="downloadDir"/> that isn't taken yet: "Name", "Name (1)", …</summary>
    public static string UniqueRootName(string downloadDir, string name)
    {
        if (!Directory.Exists(Path.Combine(downloadDir, name)) && !File.Exists(Path.Combine(downloadDir, name))) return name;
        for (var i = 1; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (!Directory.Exists(Path.Combine(downloadDir, candidate)) && !File.Exists(Path.Combine(downloadDir, candidate))) return candidate;
        }
    }
}
