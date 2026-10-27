using Bridge.Network;
using Bridge.Protocol;

namespace Bridge.Core;

/// <summary>
/// Feeds <see cref="DiagnosticsLog"/> from the host, its sessions, file
/// transfers and notification actions. Everything goes through typed
/// <see cref="LogField"/>s: counts, statuses, error types and hashed ids —
/// never names, paths, file names or message contents.
/// </summary>
public sealed class BridgeDiagnostics
{
    private readonly DiagnosticsLog _log;
    private readonly BridgeHost _host;

    public BridgeDiagnostics(DiagnosticsLog log, BridgeHost host, FileTransferService files, NotificationActionRouter? notificationActions = null,
        PhotoBrowser? photos = null, HandoffService? handoff = null)
    {
        _log = log;
        host.DevicePaired += d => _log.Info("pair.accepted", LogField.Id("peer", d.DeviceId, _log));
        host.ConnectionRejected += reason => _log.Warn("connection.rejected", RejectionFields(reason));
        _host = host;
        host.SessionStarted += OnSessionStarted;
        host.ActiveSessionChanged += active =>
            _log.Info("phone.active", LogField.Id("peer", active?.Device.DeviceId, _log), LogField.Num("phones", host.Sessions.Count));
        files.ProgressChanged += OnProgress;
        if (notificationActions is not null)
            notificationActions.Completed += (kind, status) =>
                _log.Info("notification.action", LogField.Code("kind", kind), LogField.Code("status", status));
        if (photos is not null)
            photos.Completed += (kind, status) =>
            {
                // A grid loads dozens of previews at once: log those only when they fail.
                if (kind == "thumb" && status == PhotoStatus.Ok) return;
                _log.Info("photos.request", LogField.Code("kind", kind), LogField.Code("status", status));
            };
        if (handoff is not null)
            handoff.Completed += (dir, kind, status) =>
                _log.Info("handoff", LogField.Code("dir", dir), LogField.Code("kind", kind), LogField.Code("status", status));
    }

    /// <summary>Known message types logged with '_' instead of '.'; anything else is "unknown".</summary>
    private static readonly HashSet<string> KnownTypes = typeof(MessageType).GetFields()
        .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet();

    public static string TypeCode(string? type) => type is not null && KnownTypes.Contains(type) ? type.Replace('.', '_') : "unknown";

    private LogField[] RejectionFields(string reason)
    {
        // The host's reasons are fixed sentences; map them to codes (the peer-supplied part is never logged).
        if (reason.StartsWith("Connection failed during setup:", StringComparison.Ordinal))
            return [LogField.Code("reason", "setup_failed"), LogField.Code("error", reason[(reason.LastIndexOf(' ') + 1)..])];
        var code = reason switch
        {
            "Pairing request rejected" => "pairing_rejected",
            "Unpaired device tried to connect" => "unpaired_device",
            "Identity signature check failed" => "bad_signature",
            _ when reason.StartsWith("Unexpected first message", StringComparison.Ordinal) => "unexpected_first_message",
            _ => "other",
        };
        return [LogField.Code("reason", code)];
    }

    private void OnSessionStarted(BridgeSession session)
    {
        var peer = session.Device.DeviceId;
        var started = DateTimeOffset.UtcNow;
        _log.Info("session.started", LogField.Id("peer", peer, _log), LogField.Num("features", session.Device.PeerFeatures.Count),
            LogField.Flag("resume", session.SendOptions.PeerSupportsResume), LogField.Flag("actions", session.SupportsNotificationActions),
            LogField.Num("phones", _host.Sessions.Count));
        session.Ended += _ => _log.Info("session.ended", LogField.Id("peer", peer, _log), LogField.Ms("duration_ms", DateTimeOffset.UtcNow - started),
            LogField.Num("phones", _host.Sessions.Count));
        session.HandlerFailed += (type, ex) => _log.Warn("handler.failed", LogField.Code("type", TypeCode(type)), LogField.Error(ex));
        session.PeerError += error => _log.Warn("peer.error", LogField.Code("code", error?.Code));
    }

    private void OnProgress(TransferProgress p)
    {
        if (p.Status == TransferStatus.InProgress) return; // outcomes only
        _log.Info("transfer.done",
            LogField.Id("id", p.TransferId, _log),
            LogField.Code("dir", p.Direction.ToString()),
            LogField.Code("status", p.Status.ToString()),
            LogField.Num("chunks", p.TotalChunks),
            LogField.Num("resumed_from", p.ResumedFrom),
            LogField.Flag("folder", p.BatchId is not null));
    }
}

/// <summary>A plain-text report to attach to a bug report: a few safe facts plus the log.</summary>
public static class DiagnosticsReport
{
    public static string Build(DiagnosticsLog log, DateTimeOffset now, params LogField[] facts)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Bridge diagnostics report");
        sb.AppendLine($"generated={now.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}");
        foreach (var f in facts) sb.AppendLine($"{f.Key}={f.Value}");
        sb.AppendLine("This report contains connection and transfer events only: no file names, messages, clipboard text or keys.");
        sb.AppendLine("--- log ---");
        foreach (var line in log.Lines()) sb.AppendLine(line);
        return sb.ToString();
    }
}
