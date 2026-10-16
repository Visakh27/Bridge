using Bridge.Network;

namespace Bridge.Core;

/// <summary>One row of "Recent transfers".</summary>
public sealed record TransferEntry(
    string TransferId,
    string FileName,
    TransferDirection Direction,
    TransferStatus Status,
    double Fraction,
    DateTimeOffset Updated,
    string? SavedPath = null,
    string? SourcePath = null,
    string? Folder = null)
{
    /// <summary>A single-file send that failed or was cancelled, whose file is still there, can be sent again.</summary>
    public bool CanRetry => Direction == TransferDirection.Sending && Folder is null &&
        Status is TransferStatus.Failed or TransferStatus.Cancelled && SourcePath is not null && File.Exists(SourcePath);

    /// <summary>A received file that is still there can be opened.</summary>
    public bool CanOpen => Direction == TransferDirection.Receiving && Status == TransferStatus.Completed &&
        SavedPath is not null && File.Exists(SavedPath);
}

/// <summary>
/// The last <see cref="Capacity"/> transfers, newest first, updated from
/// <see cref="FileTransferService.ProgressChanged"/>. A folder counts as one
/// row per file; progress updates replace their row in place. Thread-safe.
/// </summary>
public sealed class TransferHistory
{
    public const int Capacity = 20;
    private readonly object _gate = new();
    private readonly List<TransferEntry> _entries = [];
    private readonly Func<DateTimeOffset> _now;

    public TransferHistory(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    public event Action? Changed;

    public IReadOnlyList<TransferEntry> Entries { get { lock (_gate) return [.. _entries]; } }

    public void Update(TransferProgress p)
    {
        var entry = new TransferEntry(p.TransferId, p.FileName, p.Direction, p.Status, p.Fraction, _now(),
            p.SavedPath, p.SourcePath, p.RelativePath?.Split('/')[0]);
        lock (_gate)
        {
            var i = _entries.FindIndex(e => e.TransferId == p.TransferId && e.Direction == p.Direction);
            if (i >= 0)
            {
                var old = _entries[i];
                _entries.RemoveAt(i);
                entry = entry with { SavedPath = entry.SavedPath ?? old.SavedPath, SourcePath = entry.SourcePath ?? old.SourcePath };
            }
            _entries.Insert(0, entry);
            if (_entries.Count > Capacity) _entries.RemoveRange(Capacity, _entries.Count - Capacity);
        }
        Changed?.Invoke();
    }

    /// <summary>Drops a row (e.g. it was retried, so a new row takes over).</summary>
    public void Remove(string transferId)
    {
        lock (_gate) _entries.RemoveAll(e => e.TransferId == transferId);
        Changed?.Invoke();
    }

    /// <summary>Removes finished rows (keeps ones still in progress or paused).</summary>
    public void ClearFinished()
    {
        lock (_gate) _entries.RemoveAll(e => e.Status is not (TransferStatus.InProgress or TransferStatus.Interrupted));
        Changed?.Invoke();
    }

    /// <summary>Short status line for a row.</summary>
    public static string Describe(TransferEntry e) => (e.Direction, e.Status) switch
    {
        (_, TransferStatus.InProgress) => $"{(e.Direction == TransferDirection.Sending ? "Sending" : "Receiving")} · {e.Fraction:P0}",
        (_, TransferStatus.Interrupted) => $"Paused at {e.Fraction:P0} · continues when reconnected",
        (TransferDirection.Sending, TransferStatus.Completed) => "Sent",
        (TransferDirection.Receiving, TransferStatus.Completed) => "Received",
        (_, TransferStatus.Cancelled) => "Cancelled",
        _ => "Didn't transfer correctly",
    };
}
