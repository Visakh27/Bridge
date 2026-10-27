using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Bridge.Protocol;

namespace Bridge.Core;

/// <summary>A link checked and ready to show: <see cref="Host"/> is the ASCII (punycode) host, so look-alike domains can't hide.</summary>
public sealed record HandoffLink(string Url, string Host, string? Title);

/// <summary>
/// Rules for what may be handed over, identical on both apps
/// (android: handoff/HandoffRules.kt): plain http(s) links without
/// credentials, at most <see cref="MaxUrl"/> characters; text up to
/// <see cref="MaxText"/>; titles cleaned for display.
/// </summary>
public static class HandoffRules
{
    public const int MaxUrl = 2048;
    public const int MaxText = 10_000;
    public const int MaxTitle = 200;
    public const int MaxId = 64;

    public static HandoffLink? CheckUrl(string? url, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (url.Length > MaxUrl || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null; // https://bank.com@evil.example/ and passwords in links
        if (string.IsNullOrEmpty(uri.Host)) return null;
        string host;
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6) host = uri.Host.ToLowerInvariant();
        else
        {
            try { host = new IdnMapping().GetAscii(uri.IdnHost).ToLowerInvariant(); }
            catch (ArgumentException) { return null; }
        }
        // Rebuilt around the ASCII host, so what's sent and opened is exactly the host that's shown.
        var normalized = $"{uri.Scheme}://{host}{(uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))}{uri.PathAndQuery}{uri.Fragment}";
        return normalized.Length > MaxUrl ? null : new HandoffLink(normalized, host, CleanTitle(title));
    }

    public static string? CheckText(string? text) =>
        string.IsNullOrWhiteSpace(text) || text.Length > MaxText || text.Contains('\0') ? null : text;

    /// <summary>A title fit for a notification: no control or bidi-override characters, at most <see cref="MaxTitle"/>.</summary>
    public static string? CleanTitle(string? title)
    {
        var t = PhoneNotificationText.Strip(title);
        return t.Length == 0 ? null : PhoneNotificationText.Clip(t, MaxTitle - 1); // Clip adds "…"
    }

    /// <summary>A one-line preview of handed-over text for a notification.</summary>
    public static string Preview(string text, int max = 80) => PhoneNotificationText.Clip(PhoneNotificationText.Strip(text), max);
}

/// <summary>
/// Receives handoffs from the phone: checks them (<see cref="HandoffRules"/>),
/// drops repeats of the same id, refuses floods (more than
/// <see cref="MaxPerMinute"/> a minute) and answers every one with handoff.ack.
/// What the user sees is up to <see cref="LinkReceived"/> / <see cref="TextReceived"/>;
/// sending goes through <see cref="SendUrlAsync"/> / <see cref="SendTextAsync"/>.
/// </summary>
public sealed class HandoffService
{
    public const int MaxPerMinute = 20;
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(10);

    private readonly BridgeHost _host;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _autoOpen;
    private readonly Func<DateTimeOffset> _now;
    /// <summary>Accepted handoffs in the last minute, per phone (each phone has its own allowance).</summary>
    private readonly Dictionary<string, Queue<DateTimeOffset>> _recent = new();
    private readonly LinkedList<(string Id, string Status)> _seen = new();
    private readonly ConcurrentDictionary<string, (BridgeSession To, TaskCompletionSource<string> Answer)> _pending = new();

    /// <param name="enabled">Settings: receive links and text from the phone.</param>
    /// <param name="autoOpen">Settings: open links from the phone right away (else a notification offers them).</param>
    public HandoffService(BridgeHost host, Func<bool>? enabled = null, Func<bool>? autoOpen = null, Func<DateTimeOffset>? now = null)
    {
        _host = host;
        _enabled = enabled ?? (() => true);
        _autoOpen = autoOpen ?? (() => false);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        host.SessionStarted += session => session.HandoffMessage += m => _ = HandleAsync(session, m);
    }

    /// <summary>A link to offer (opened: true when auto-open is on and the UI should open it).</summary>
    public event Action<HandoffLink, bool, string>? LinkReceived;
    /// <summary>Text to put on the clipboard (text, phone name).</summary>
    public event Action<string, string>? TextReceived;
    /// <summary>Diagnostics: (direction, kind, status) — never the link or text.</summary>
    public event Action<string, string, string>? Completed;

    private async Task HandleAsync(BridgeSession session, BridgeMessage message)
    {
        if (message.Type == MessageType.HandoffAck)
        {
            HandoffAckPayload? ack = null;
            try { ack = message.PayloadAs<HandoffAckPayload>(); } catch (InvalidDataException) { }
            // An answer counts only from the phone it was sent to.
            if (ack?.Id is { } id && _pending.TryGetValue(id, out var waiting) && ReferenceEquals(waiting.To, session))
                waiting.Answer.TrySetResult(WireStatus(ack.Status));
            return;
        }
        if (message.Type != MessageType.HandoffOpen) return;
        HandoffPayload? p = null;
        try { p = message.PayloadAs<HandoffPayload>(); } catch (InvalidDataException) { }
        if (p?.Id is not { Length: > 0 and <= HandoffRules.MaxId } id2) return; // nothing to answer to
        var status = Decide(p, session.Device.DeviceId, session.Device.DeviceName);
        Completed?.Invoke("in", p.Kind is HandoffKind.Url or HandoffKind.Text ? p.Kind : "unknown", status);
        try { await session.SendAsync(BridgeMessage.Create(MessageType.HandoffAck, new HandoffAckPayload(id2, status))); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>A status the phone may send; anything else (or a missing one) counts as invalid.</summary>
    public static string WireStatus(string? status) => status switch
    {
        HandoffStatus.Shown or HandoffStatus.Opened or HandoffStatus.Copied or HandoffStatus.Disabled or HandoffStatus.Busy => status,
        _ => HandoffStatus.Invalid,
    };

    private string Decide(HandoffPayload p, string deviceId, string phone)
    {
        var seenKey = $"{deviceId}\n{p.Id}"; // ids are unique per sender only
        // One at a time, start to finish: two copies of an id arriving together still act once.
        lock (_recent)
        {
            // A repeat of one already handled (the sender retried): same answer, nothing shown twice.
            foreach (var (seenId, seenStatus) in _seen) if (seenId == seenKey) return seenStatus;
            var status = DecideNew(p, deviceId, phone);
            _seen.AddFirst((seenKey, status));
            if (_seen.Count > 100) _seen.RemoveLast();
            return status;
        }
    }

    private string DecideNew(HandoffPayload p, string deviceId, string phone)
    {
        if (!_enabled()) return HandoffStatus.Disabled;
        switch (p.Kind)
        {
            case HandoffKind.Url when HandoffRules.CheckUrl(p.Url, p.Title) is { } link:
                if (!Admit(deviceId)) return HandoffStatus.Busy;
                var open = _autoOpen();
                LinkReceived?.Invoke(link, open, phone);
                return open ? HandoffStatus.Opened : HandoffStatus.Shown;
            case HandoffKind.Text when HandoffRules.CheckText(p.Text) is { } text:
                if (!Admit(deviceId)) return HandoffStatus.Busy;
                TextReceived?.Invoke(text, phone);
                return HandoffStatus.Copied;
            default:
                return HandoffStatus.Invalid;
        }
    }

    /// <summary>Sliding one-minute window per phone: at most <see cref="MaxPerMinute"/> accepted.</summary>
    private bool Admit(string deviceId)
    {
        lock (_recent)
        {
            var now = _now();
            if (!_recent.TryGetValue(deviceId, out var times)) _recent[deviceId] = times = new Queue<DateTimeOffset>();
            while (times.Count > 0 && now - times.Peek() >= TimeSpan.FromMinutes(1)) times.Dequeue();
            if (times.Count >= MaxPerMinute) return false;
            times.Enqueue(now);
            return true;
        }
    }

    /// <summary>
    /// Hands a link to a phone (<paramref name="deviceId"/>; null = the active phone); returns its <see cref="HandoffStatus"/>.
    /// </summary>
    public Task<string> SendUrlAsync(string url, string? title = null, CancellationToken ct = default, string? deviceId = null) =>
        HandoffRules.CheckUrl(url, title) is { } link
            ? SendAsync(new HandoffPayload(NewId(), HandoffKind.Url) { Url = link.Url, Title = link.Title }, deviceId, ct)
            : Task.FromResult(HandoffStatus.Invalid);

    /// <summary>Hands text to a phone (it goes on the phone's clipboard).</summary>
    public Task<string> SendTextAsync(string text, CancellationToken ct = default, string? deviceId = null) =>
        HandoffRules.CheckText(text) is { } t
            ? SendAsync(new HandoffPayload(NewId(), HandoffKind.Text) { Text = t }, deviceId, ct)
            : Task.FromResult(HandoffStatus.Invalid);

    /// <summary>Clipboard content as a handoff: a lone link goes as a link, anything else as text.</summary>
    public Task<string> SendClipboardAsync(string? clipboard, CancellationToken ct = default, string? deviceId = null)
    {
        var trimmed = clipboard?.Trim();
        return HandoffRules.CheckUrl(trimmed) is not null ? SendUrlAsync(trimmed!, null, ct, deviceId) : SendTextAsync(clipboard ?? "", ct, deviceId);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private async Task<string> SendAsync(HandoffPayload payload, string? deviceId, CancellationToken ct)
    {
        var session = deviceId is null ? _host.CurrentSession : _host.SessionFor(deviceId);
        string status;
        if (session is not { IsRunning: true } || !session.Device.PeerFeatures.Contains(Features.Handoff)) status = HandoffStatus.Unavailable;
        else
        {
            var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[payload.Id] = (session, answer);
            try
            {
                await session.SendAsync(BridgeMessage.Create(MessageType.HandoffOpen, payload), ct);
                status = await answer.Task.WaitAsync(AckTimeout, ct);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or InvalidOperationException) { status = HandoffStatus.Unavailable; }
            finally { _pending.TryRemove(payload.Id, out _); }
        }
        Completed?.Invoke("out", payload.Kind, status);
        return status;
    }

    /// <summary>What to tell the user after sending (null = it worked).</summary>
    public static string? Explain(string status) => status switch
    {
        HandoffStatus.Shown or HandoffStatus.Opened or HandoffStatus.Copied => null,
        HandoffStatus.Disabled => "Your phone isn't accepting links and text from this PC (Bridge › Settings on the phone).",
        HandoffStatus.Invalid => "Bridge can send web links (http or https) and text up to 10,000 characters.",
        HandoffStatus.Busy => "Too many at once. Try again in a minute.",
        _ => "Your phone isn't connected (or needs a newer Bridge), so that wasn't sent.",
    };
}
