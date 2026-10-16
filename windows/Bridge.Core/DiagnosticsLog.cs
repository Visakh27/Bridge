using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bridge.Core;

/// <summary>
/// One field of a diagnostics event. Only these shapes exist, so nothing a
/// user typed, copied or received (file names, clipboard text, notification
/// text, device names, keys, tokens) can be written by accident:
/// numbers, flags, short code words (statuses, exception type names), and
/// identifiers, which are logged as a short salted hash.
/// </summary>
public readonly record struct LogField
{
    public string Key { get; }
    public string Value { get; }
    private LogField(string key, string value) { Key = key; Value = value; }

    private static readonly Regex CodePattern = new("^[A-Za-z_][A-Za-z0-9_-]{0,31}$", RegexOptions.CultureInvariant);

    public static LogField Num(string key, long value) => new(key, value.ToString(CultureInfo.InvariantCulture));
    public static LogField Flag(string key, bool value) => new(key, value ? "true" : "false");
    public static LogField Ms(string key, TimeSpan value) => new(key, ((long)value.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));

    /// <summary>A code word such as a status or an exception type name. Anything else becomes "?".</summary>
    public static LogField Code(string key, string? value) => new(key, value is not null && CodePattern.IsMatch(value) ? value : "?");

    /// <summary>A device/transfer id, written as 8 hex characters of a salted hash (correlates within one log, reveals nothing).</summary>
    public static LogField Id(string key, string? value, DiagnosticsLog log) => new(key, value is null ? "-" : log.HashId(value));

    /// <summary>A version number, e.g. 0.2.0 or 10.0.22631.</summary>
    public static LogField Version(string key, System.Version? value) => new(key, value?.ToString() ?? "-");

    /// <summary>An exception: its type name only (messages can contain paths and names).</summary>
    public static LogField Error(Exception ex) => Code("error", ex.GetType().Name);

    internal static bool IsValidKey(string key) => CodePattern.IsMatch(key);
}

/// <summary>
/// Diagnostics log (docs/security.md, Logging): metadata only — timestamps,
/// event names, counts, sizes, statuses and error types — never contents.
/// Written to <c>bridge.log</c> in <see cref="Directory"/>, rotated at
/// <see cref="MaxFileBytes"/> keeping <see cref="Files"/> files; older than
/// <see cref="Retention"/> is deleted. Thread-safe; never throws.
/// </summary>
public sealed class DiagnosticsLog
{
    public const long MaxFileBytes = 256 * 1024;
    public const int Files = 3;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    public const string FileName = "bridge.log";

    private readonly object _gate = new();
    private readonly byte[] _salt;
    private readonly Func<DateTimeOffset> _now;
    private readonly List<string>? _memory; // directory-less logs (tests) keep lines here

    /// <param name="directory">Where logs live; null keeps lines in memory only.</param>
    public DiagnosticsLog(string? directory, Func<DateTimeOffset>? now = null)
    {
        Directory = directory;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _salt = RandomNumberGenerator.GetBytes(16); // per run: ids correlate within a run only
        if (directory is null) _memory = [];
        else
        {
            try { System.IO.Directory.CreateDirectory(directory); DeleteExpired(); } catch (Exception) { }
        }
    }

    public string? Directory { get; }

    /// <summary>When false nothing is written (Settings › Diagnostics).</summary>
    public bool Enabled { get; set; } = true;

    public string HashId(string value)
    {
        var bytes = SHA256.HashData([.. _salt, .. Encoding.UTF8.GetBytes(value)]);
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    public void Info(string eventName, params LogField[] fields) => Write("INFO", eventName, fields);
    public void Warn(string eventName, params LogField[] fields) => Write("WARN", eventName, fields);

    private static readonly Regex EventPattern = new("^[a-z][a-z0-9_.]{0,47}$", RegexOptions.CultureInvariant);

    private void Write(string level, string eventName, LogField[] fields)
    {
        if (!Enabled) return;
        var sb = new StringBuilder(96);
        sb.Append(_now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
          .Append(' ').Append(level).Append(' ').Append(EventPattern.IsMatch(eventName) ? eventName : "invalid_event");
        foreach (var f in fields)
        {
            if (f.Key is null || !LogField.IsValidKey(f.Key)) continue;
            sb.Append(' ').Append(f.Key).Append('=').Append(f.Value);
        }
        var line = sb.ToString();
        lock (_gate)
        {
            if (_memory is not null) { _memory.Add(line); if (_memory.Count > 5000) _memory.RemoveAt(0); return; }
            try
            {
                var path = Path.Combine(Directory!, FileName);
                var info = new FileInfo(path);
                if (info.Exists && info.Length + line.Length + 1 > MaxFileBytes) Rotate();
                File.AppendAllText(path, line + "\n");
            }
            catch (Exception) { /* diagnostics must never break the app */ }
        }
    }

    private void Rotate()
    {
        var last = Path.Combine(Directory!, $"bridge.{Files - 1}.log");
        if (File.Exists(last)) File.Delete(last);
        for (var i = Files - 2; i >= 1; i--)
        {
            var from = Path.Combine(Directory!, $"bridge.{i}.log");
            if (File.Exists(from)) File.Move(from, Path.Combine(Directory!, $"bridge.{i + 1}.log"));
        }
        File.Move(Path.Combine(Directory!, FileName), Path.Combine(Directory!, "bridge.1.log"));
    }

    private void DeleteExpired()
    {
        foreach (var f in LogFiles())
            if (_now().UtcDateTime - File.GetLastWriteTimeUtc(f) > Retention) File.Delete(f);
    }

    /// <summary>Log files, oldest first.</summary>
    public IReadOnlyList<string> LogFiles()
    {
        if (Directory is null) return [];
        var files = new List<string>();
        for (var i = Files - 1; i >= 1; i--)
        {
            var p = Path.Combine(Directory, $"bridge.{i}.log");
            if (File.Exists(p)) files.Add(p);
        }
        var current = Path.Combine(Directory, FileName);
        if (File.Exists(current)) files.Add(current);
        return files;
    }

    /// <summary>All logged lines, oldest first.</summary>
    public IReadOnlyList<string> Lines()
    {
        lock (_gate)
        {
            if (_memory is not null) return [.. _memory];
            var lines = new List<string>();
            foreach (var f in LogFiles())
                try { lines.AddRange(File.ReadAllLines(f)); } catch (Exception) { }
            return lines;
        }
    }

    /// <summary>Deletes all log files (e.g. when the user turns diagnostics off).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _memory?.Clear();
            foreach (var f in LogFiles()) try { File.Delete(f); } catch (Exception) { }
        }
    }
}
