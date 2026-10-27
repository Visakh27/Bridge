using System.Text;
using Bridge.Network;

namespace Bridge.Core;

/// <summary>What File Explorer handed Bridge to send ("Send to" or "Share").</summary>
public sealed record SendRequest(IReadOnlyList<string> Files, IReadOnlyList<string> Folders, IReadOnlyList<string> Skipped)
{
    public int Count => Files.Count + Folders.Count;
}

/// <summary>How a send went. <see cref="Interrupted"/> files continue when the phone is back.</summary>
public sealed record SendSummary(int Total, int Sent, int Failed, int Interrupted, int Cancelled, bool WasQueued, string? DeviceName);

/// <summary>
/// Bridge's command line. Explorer's "Send to" runs <c>Bridge.Windows.exe --send "C:\a.jpg" "D:\Folder"</c>;
/// a second launch is redirected to the running Bridge, which gets the whole command line as one string.
/// </summary>
public static class LaunchArguments
{
    public const string SendFlag = "--send";
    public const string MinimizedFlag = "--minimized";
    public const int MaxItems = 1000;

    /// <summary>Splits a command line the way Windows (CommandLineToArgvW) does.</summary>
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        var args = new List<string>();
        if (string.IsNullOrEmpty(commandLine)) return args;
        var current = new StringBuilder();
        bool inQuotes = false, any = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var start = i;
                while (i < commandLine.Length && commandLine[i] == '\\') i++;
                var slashes = i - start;
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1) current.Append('"'); // \" is a literal quote
                    else { inQuotes = !inQuotes; }
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                }
                any = true;
            }
            else if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"') { current.Append('"'); i++; } // "" inside quotes
                else inQuotes = !inQuotes;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (any) { args.Add(current.ToString()); current.Clear(); any = false; }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any) args.Add(current.ToString());
        return args;
    }

    /// <summary>
    /// The paths after <c>--send</c>, or null if there's no <c>--send</c>. Only absolute paths that
    /// exist count; duplicates are dropped; beyond <see cref="MaxItems"/> the rest are skipped.
    /// </summary>
    public static SendRequest? ParseSend(IReadOnlyList<string> args, Func<string, bool>? fileExists = null, Func<string, bool>? folderExists = null)
    {
        fileExists ??= File.Exists;
        folderExists ??= Directory.Exists;
        var at = -1;
        for (var i = 0; i < args.Count; i++) if (string.Equals(args[i], SendFlag, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
        if (at < 0) return null;
        var files = new List<string>();
        var folders = new List<string>();
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in args.Skip(at + 1))
        {
            var p = raw.Trim();
            if (p.Length == 0 || p.StartsWith("--", StringComparison.Ordinal)) continue; // another flag
            string full;
            try
            {
                if (!Path.IsPathFullyQualified(p) || p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) { skipped.Add(p); continue; }
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { skipped.Add(p); continue; }
            if (!seen.Add(full)) continue;
            if (files.Count + folders.Count >= MaxItems) { skipped.Add(full); continue; }
            if (folderExists(full)) folders.Add(full);
            else if (fileExists(full)) files.Add(full);
            else skipped.Add(full);
        }
        return new SendRequest(files, folders, skipped);
    }

    /// <summary>Splits and parses in one go (redirected activations carry one string).</summary>
    public static SendRequest? ParseSend(string? commandLine, Func<string, bool>? fileExists = null, Func<string, bool>? folderExists = null) =>
        ParseSend(Split(commandLine), fileExists, folderExists);

    public static bool HasFlag(IReadOnlyList<string> args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Sends what Explorer handed over: at once if the phone is connected,
/// otherwise held until it connects again (for up to <see cref="Wait"/>), so
/// "Send to → Bridge" works even while the phone is briefly away. Several
/// requests made while waiting go out together, in order. With several phones,
/// a request goes to the active phone; one that has to wait is kept for the
/// phone the user picked (any phone if none was picked).
/// </summary>
public sealed class ExplorerSendQueue
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(30);

    private readonly BridgeHost _host;
    private readonly object _gate = new();
    private readonly List<(SendRequest Request, CancellationTokenSource Expiry, string? Target)> _pending = [];
    private readonly TimeSpan _startDelay;

    public ExplorerSendQueue(BridgeHost host, TimeSpan? wait = null, TimeSpan? startDelay = null)
    {
        _host = host;
        Wait = wait ?? DefaultWait;
        _startDelay = startDelay ?? TimeSpan.FromMilliseconds(500);
        host.SessionStarted += session => _ = Task.Run(async () =>
        {
            await Task.Delay(_startDelay); // let the session settle (resumed transfers first)
            await FlushAsync(session);
        });
    }

    public TimeSpan Wait { get; }

    /// <summary>A request is waiting for the phone (how many items are waiting in all).</summary>
    public event Action<SendRequest, int>? Queued;
    /// <summary>Sending started (request, phone name).</summary>
    public event Action<SendRequest, string>? Started;
    public event Action<SendSummary>? Finished;
    /// <summary>The phone didn't come back in time; the request was dropped.</summary>
    public event Action<SendRequest>? Expired;

    public int PendingItems { get { lock (_gate) return _pending.Sum(p => p.Request.Count); } }

    /// <summary>Sends now (returns the summary) or queues for the next connection (returns null).</summary>
    public async Task<SendSummary?> SubmitAsync(SendRequest request, CancellationToken ct = default)
    {
        if (request.Count == 0) return new SendSummary(0, 0, 0, 0, 0, false, null);
        var session = _host.CurrentSession;
        if (session is { IsRunning: true }) return await SendNowAsync(session, request, false, ct);

        var target = _host.ActiveDeviceId;
        var waiting = Enqueue(request, target);
        Queued?.Invoke(request, waiting);
        // The phone may have connected between the check above and queuing: don't wait for the next connection.
        if (_host.CurrentSession is { IsRunning: true } arrived) _ = Task.Run(() => FlushAsync(arrived));
        return null;
    }

    /// <summary>Adds a waiting request that is dropped (Expired) if the phone isn't back within <see cref="Wait"/>.</summary>
    private int Enqueue(SendRequest request, string? target)
    {
        var expiry = new CancellationTokenSource();
        int waiting;
        lock (_gate)
        {
            _pending.Add((request, expiry, target));
            waiting = _pending.Sum(p => p.Request.Count);
        }
        _ = Task.Delay(Wait, expiry.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            bool removed;
            lock (_gate) removed = _pending.RemoveAll(p => ReferenceEquals(p.Request, request)) > 0;
            if (removed) Expired?.Invoke(request);
        }, TaskScheduler.Default);
        return waiting;
    }

    /// <summary>Drops everything waiting (the "Cancel" on the waiting notification).</summary>
    public int CancelPending()
    {
        List<(SendRequest Request, CancellationTokenSource Expiry, string? Target)> dropped;
        lock (_gate) { dropped = [.. _pending]; _pending.Clear(); }
        foreach (var d in dropped) d.Expiry.Cancel();
        return dropped.Sum(d => d.Request.Count);
    }

    private readonly SemaphoreSlim _flushing = new(1, 1);

    private async Task FlushAsync(BridgeSession session)
    {
        await _flushing.WaitAsync(); // one flush at a time keeps requests in order
        try
        {
            List<(SendRequest Request, CancellationTokenSource Expiry, string? Target)> due;
            lock (_gate)
            {
                // Only what's meant for this phone (or for any phone); the rest keeps waiting for its own.
                due = _pending.Where(p => p.Target is null || p.Target == session.Device.DeviceId).ToList();
                _pending.RemoveAll(p => p.Target is null || p.Target == session.Device.DeviceId);
            }
            foreach (var (request, expiry, target) in due)
            {
                expiry.Cancel();
                expiry.Dispose();
                if (!session.IsRunning)
                {
                    Enqueue(request, target); // the phone dropped again first: wait for it (with a fresh time limit)
                    continue;
                }
                await SendNowAsync(session, request, true, CancellationToken.None);
            }
        }
        finally
        {
            _flushing.Release();
        }
    }

    private async Task<SendSummary> SendNowAsync(BridgeSession session, SendRequest request, bool wasQueued, CancellationToken ct)
    {
        Started?.Invoke(request, session.Device.DeviceName);
        var statuses = new List<TransferStatus>();
        foreach (var file in request.Files)
        {
            if (ct.IsCancellationRequested) break;
            if (!File.Exists(file)) { statuses.Add(TransferStatus.Failed); continue; } // deleted while waiting
            statuses.Add((await session.SendFileAsync(file, ct)).Status);
        }
        foreach (var folder in request.Folders)
        {
            if (ct.IsCancellationRequested) break;
            if (!Directory.Exists(folder)) { statuses.Add(TransferStatus.Failed); continue; }
            var results = await session.SendFolderAsync(folder, ct);
            statuses.AddRange(results.Count == 0 ? [TransferStatus.Completed] : results.Select(r => r.Status)); // an empty folder sends nothing
        }
        var summary = new SendSummary(statuses.Count,
            statuses.Count(s => s == TransferStatus.Completed),
            statuses.Count(s => s == TransferStatus.Failed),
            statuses.Count(s => s == TransferStatus.Interrupted),
            statuses.Count(s => s == TransferStatus.Cancelled),
            wasQueued, session.Device.DeviceName);
        Finished?.Invoke(summary);
        return summary;
    }

    /// <summary>A short line for a notification.</summary>
    public static string Describe(SendSummary s)
    {
        string Items(int n) => n == 1 ? "1 file" : $"{n} files";
        if (s.Total == 0) return "Nothing to send.";
        if (s.Sent == s.Total) return $"Sent {Items(s.Sent)} to {s.DeviceName ?? "your phone"}.";
        var parts = new List<string>();
        if (s.Sent > 0) parts.Add($"sent {Items(s.Sent)}");
        if (s.Interrupted > 0) parts.Add($"{Items(s.Interrupted)} will finish when your phone reconnects");
        if (s.Failed > 0) parts.Add($"{Items(s.Failed)} didn't transfer (see Recent transfers to retry)");
        if (s.Cancelled > 0) parts.Add($"{Items(s.Cancelled)} cancelled");
        var text = string.Join("; ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }
}
