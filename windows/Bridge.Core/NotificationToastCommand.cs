using Bridge.Protocol;

namespace Bridge.Core;

/// <summary>
/// What a click on a mirrored-notification toast asks for. The toast carries
/// these as activation arguments (<see cref="ToArguments"/>); Windows hands
/// them back with any typed text when the user clicks, possibly to a freshly
/// launched Bridge. Parsing is strict: unknown kinds or missing fields mean
/// "just open Bridge".
/// </summary>
public sealed record NotificationToastCommand(string Kind, string? Key = null, string? ActionId = null, string? AppId = null)
{
    public const string Open = "open";
    public const string Reply = "reply";
    public const string Press = "press";
    public const string Dismiss = "dismiss";
    public const string Mute = "mute";
    /// <summary>Drop what Explorer handed over while the phone was away (the "waiting to send" toast).</summary>
    public const string CancelSend = "cancel-send";

    /// <summary>Id of the toast's reply text box.</summary>
    public const string ReplyInputId = "reply";

    /// <summary>
    /// Toast arguments. Values from the phone are base64url-encoded, so whatever characters an
    /// app puts in its notification tag can't break Windows' "key=value;…" argument format.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToArguments()
    {
        var args = new Dictionary<string, string> { ["bridge"] = Kind };
        if (Key is not null) args["key"] = Encode(Key);
        if (ActionId is not null) args["id"] = Encode(ActionId);
        if (AppId is not null) args["app"] = Encode(AppId);
        return args;
    }

    public static NotificationToastCommand Parse(IDictionary<string, string>? args)
    {
        if (args is null || !args.TryGetValue("bridge", out var kind)) return new(Open);
        var key = Decode(args, "key");
        var id = Decode(args, "id");
        var app = Decode(args, "app");
        return kind switch
        {
            Reply or Press when !string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(id) => new(kind, key, id),
            Dismiss when !string.IsNullOrEmpty(key) => new(kind, key),
            Mute when !string.IsNullOrEmpty(app) => new(kind, AppId: app),
            CancelSend => new(kind),
            _ => new(Open),
        };
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Decode(IDictionary<string, string> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value.Length is 0 or > 4096) return null;
        var b64 = value.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        try { return new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(b64)); }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return null; }
    }

    /// <summary>
    /// Carries the command out. Returns a <see cref="NotificationActionStatus"/>, or null for
    /// <see cref="Open"/> (nothing to send). <paramref name="userInput"/> holds the toast's typed text.
    /// </summary>
    public async Task<string?> RunAsync(NotificationActionRouter router, IDictionary<string, string>? userInput, CancellationToken ct = default)
    {
        switch (Kind)
        {
            case Reply:
                string? text = null;
                userInput?.TryGetValue(ReplyInputId, out text);
                return await router.ReplyAsync(Key!, ActionId!, text, ct);
            case Press:
                return await router.PressAsync(Key!, ActionId!, ct);
            case Dismiss:
                return await router.DismissAsync(Key!, ct) ? NotificationActionStatus.Ok : NotificationActionStatus.Unavailable;
            case Mute:
                return await router.MuteAppAsync(AppId!, ct) ? NotificationActionStatus.Ok : NotificationActionStatus.Unavailable;
            default:
                return null;
        }
    }

    /// <summary>A short message for the user when the command didn't go through (null = nothing to say).</summary>
    public string? FailureMessage(string? status, string appName = "the app") => (Kind, status) switch
    {
        (_, null or NotificationActionStatus.Ok) => null,
        (_, NotificationActionStatus.Unavailable) => "Your phone isn't connected, so that wasn't sent.",
        (Reply, NotificationActionStatus.Invalid) => "Type a reply first (up to 2,000 characters).",
        (_, NotificationActionStatus.Gone) => "That notification is no longer on your phone.",
        (_, NotificationActionStatus.Refused) => $"Your phone isn't sharing {appName} notifications any more.",
        (Reply, _) => $"{appName} didn't accept the reply.",
        _ => $"{appName} didn't accept that.",
    };
}
