using System.Globalization;
using System.Text;
using Bridge.Protocol;

namespace Bridge.Core;

/// <summary>
/// Notification contents come from arbitrary phone apps, so the PC treats them
/// as untrusted before they reach a Windows toast: control characters and bidi
/// overrides (which can make text read differently from what it is) are
/// removed, lengths are capped, at most <see cref="MaxActions"/> well-formed
/// buttons are kept, and anything without a usable key or title is dropped.
/// </summary>
public static class PhoneNotificationText
{
    public const int MaxTitle = 200;
    public const int MaxText = 1000;
    public const int MaxAppName = 100;
    public const int MaxActionTitle = 40;
    public const int MaxActions = 3;
    public const int MaxKey = 1024;
    public const int MaxId = 64;
    public const int MaxAppId = 255;
    public const int MaxReply = 2000;

    /// <summary>A safe copy of <paramref name="note"/>, or null if it can't be shown.</summary>
    public static NotificationPostPayload? Clean(NotificationPostPayload? note)
    {
        if (note is null) return null;
        if (string.IsNullOrEmpty(note.Key) || note.Key.Length > MaxKey) return null;
        var title = Clip(Strip(note.Title), MaxTitle);
        if (string.IsNullOrWhiteSpace(title)) return null;

        var actions = (note.Actions ?? [])
            .Where(a => a is not null && !string.IsNullOrEmpty(a.Id) && a.Id.Length <= MaxId && !string.IsNullOrWhiteSpace(Strip(a.Title)))
            .GroupBy(a => a.Id).Select(g => g.First()) // ids must be unique to be addressable
            .Take(MaxActions)
            .Select(a => a with { Title = Clip(Strip(a.Title), MaxActionTitle) })
            .ToArray();
        var appId = note.AppId is { Length: > 0 and <= MaxAppId } id && id.All(IsPackageChar) ? id : null;

        return note with
        {
            AppName = Clip(Strip(note.AppName), MaxAppName) is { Length: > 0 } name ? name : "Phone",
            Title = title,
            Text = Clip(Strip(note.Text, keepNewlines: true), MaxText),
            AppId = appId,
            Actions = actions.Length > 0 ? actions : null,
        };
    }

    private static bool IsPackageChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_';

    /// <summary>Removes control characters and bidi embedding/override/isolate controls.</summary>
    public static string Strip(string? s, bool keepNewlines = false)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\n' && keepNewlines) { sb.Append(c); continue; }
            if (c is '\r' or '\n' or '\t') { sb.Append(' '); continue; }
            if (char.IsControl(c)) continue;
            if (c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩') continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Cuts to <paramref name="max"/> UTF-16 units without splitting a character.</summary>
    public static string Clip(string s, int max)
    {
        if (s.Length <= max) return s;
        var end = max;
        if (char.IsHighSurrogate(s[end - 1])) end--;
        return s[..end].TrimEnd() + "…";
    }
}
