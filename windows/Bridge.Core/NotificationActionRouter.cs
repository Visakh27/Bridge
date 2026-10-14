using Bridge.Protocol;

namespace Bridge.Core;

/// <summary>
/// Carries out what the user did on a mirrored notification's toast (reply,
/// press a button, dismiss, mute the app). Toasts outlive connections — the
/// user may click one while the phone is briefly reconnecting, or after
/// Windows relaunched Bridge from the toast — so a request waits up to
/// <see cref="SessionWait"/> for a session whose phone supports actions.
/// </summary>
public sealed class NotificationActionRouter
{
    public static readonly TimeSpan DefaultSessionWait = TimeSpan.FromSeconds(20);

    private readonly BridgeHost _host;
    private readonly TimeSpan? _actionTimeout;

    public NotificationActionRouter(BridgeHost host, TimeSpan? sessionWait = null, TimeSpan? actionTimeout = null)
    {
        _host = host;
        SessionWait = sessionWait ?? DefaultSessionWait;
        _actionTimeout = actionTimeout;
    }

    public TimeSpan SessionWait { get; }

    /// <summary>After a reply/press, the app's own update of that notification is expected for this long.</summary>
    public static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, DateTime> _actedOn = new();

    /// <summary>
    /// True if the user just replied to / pressed a button on this notification from the PC, so its
    /// next update (typically the app echoing "You: …") can go quietly to the notification centre
    /// instead of popping up again. Consumed by the call.
    /// </summary>
    public bool TakeRecentAction(string key)
    {
        lock (_actedOn)
        {
            if (!_actedOn.Remove(key, out var at)) return false;
            return DateTime.UtcNow - at < EchoWindow;
        }
    }

    /// <summary>Runs an action, marking the key first: the app's update can arrive before the phone's answer.</summary>
    private async Task<string> ActAsync(string key, Func<BridgeSession, Task<string>> act, CancellationToken ct)
    {
        var session = await SessionAsync(ct);
        if (session is null) return NotificationActionStatus.Unavailable;
        DateTime mark;
        lock (_actedOn)
        {
            if (_actedOn.Count > 200)
                foreach (var old in _actedOn.Where(e => DateTime.UtcNow - e.Value >= EchoWindow).Select(e => e.Key).ToList()) _actedOn.Remove(old);
            _actedOn[key] = mark = DateTime.UtcNow;
        }
        var status = await act(session);
        if (status != NotificationActionStatus.Ok)
            lock (_actedOn) { if (_actedOn.TryGetValue(key, out var at) && at == mark) _actedOn.Remove(key); }
        return status;
    }

    /// <summary>Sends <paramref name="text"/> through a reply button. Returns a <see cref="NotificationActionStatus"/>.</summary>
    public async Task<string> ReplyAsync(string key, string actionId, string? text, CancellationToken ct = default)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > PhoneNotificationText.MaxReply) return NotificationActionStatus.Invalid;
        return await ActAsync(key, s => s.InvokeNotificationActionAsync(key, actionId, text, _actionTimeout, ct), ct);
    }

    /// <summary>Presses a (non-reply) button.</summary>
    public async Task<string> PressAsync(string key, string actionId, CancellationToken ct = default) =>
        await ActAsync(key, s => s.InvokeNotificationActionAsync(key, actionId, null, _actionTimeout, ct), ct);

    /// <summary>Clears the notification on the phone.</summary>
    public async Task<bool> DismissAsync(string key, CancellationToken ct = default) =>
        await SessionAsync(ct) is { } session && await session.DismissOnPhoneAsync(key, ct);

    /// <summary>Stops mirroring an app's notifications.</summary>
    public async Task<bool> MuteAppAsync(string appId, CancellationToken ct = default) =>
        await SessionAsync(ct) is { } session && await session.MuteAppOnPhoneAsync(appId, ct);

    /// <summary>The live session if its phone supports actions, else the next one that starts within <see cref="SessionWait"/>.</summary>
    private async Task<BridgeSession?> SessionAsync(CancellationToken ct)
    {
        if (Usable(_host.CurrentSession) is { } now) return now;
        var next = new TaskCompletionSource<BridgeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStarted(BridgeSession s) { if (Usable(s) is { } ok) next.TrySetResult(ok); }
        _host.SessionStarted += OnStarted;
        try
        {
            if (Usable(_host.CurrentSession) is { } raced) return raced; // started while subscribing
            return await next.Task.WaitAsync(SessionWait, ct);
        }
        catch (TimeoutException) { return null; }
        finally { _host.SessionStarted -= OnStarted; }
    }

    private static BridgeSession? Usable(BridgeSession? s) => s is { IsRunning: true, SupportsNotificationActions: true } ? s : null;
}
