using System.Text.Json;

namespace Bridge.Network;

/// <summary>
/// Small persistent state that makes transfers survive disconnects and app
/// restarts (protocol.md "Resume"):
///  - Outbox: files we are sending that the peer hasn't acknowledged yet.
///  - Completed: transfer ids we received recently, so a re-offer after a lost
///    ack is answered "ok" instead of storing the file twice.
///  - Batch roots: which top-level folder name a folder transfer was given, so
///    files resumed later land in the same folder.
/// Entries expire after <see cref="Lifetime"/>. With no directory it is in-memory only.
/// Thread-safe; writes are atomic (temp file + replace).
/// </summary>
public sealed class TransferStateStore : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    /// <summary>Changes are written at most this often (a big folder changes state once per file).</summary>
    public static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(250);
    public const int MaxCompleted = 500;

    /// <param name="Sha256">Cached hash of the file, valid while <paramref name="Size"/> and <paramref name="LastWriteUtc"/> still match.</param>
    public sealed record OutboxEntry(string TransferId, string PeerId, string FilePath, string FileName,
        string? RelativePath, string? BatchId, int? BatchIndex, int? BatchCount, DateTimeOffset Created,
        string? Sha256 = null, long? Size = null, DateTime? LastWriteUtc = null);

    /// <summary>A stored file; <paramref name="Sha256"/> so a reused id with other content isn't mistaken for it.</summary>
    public sealed record CompletedEntry(string Key, string SavedPath, DateTimeOffset At, string? Sha256 = null);

    public sealed record BatchRoot(string Key, string RootName, DateTimeOffset At);

    private sealed class State
    {
        public List<OutboxEntry> Outbox { get; set; } = [];
        public List<CompletedEntry> Completed { get; set; } = [];
        public List<BatchRoot> Batches { get; set; } = [];
    }

    private readonly string? _path;
    private readonly string? _completedLog; // append-only: written at once, survives the process being killed
    private readonly string? _batchLog;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private State _state;
    private readonly Timer _flushTimer;
    private bool _dirty;
    private bool _flushScheduled;

    public TransferStateStore(string? directory = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _path = directory is null ? null : Path.Combine(directory, "transfer-state.json");
        _completedLog = directory is null ? null : Path.Combine(directory, "completed.log");
        _batchLog = directory is null ? null : Path.Combine(directory, "batches.log");
        _flushTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _state = Load();
        _state.Completed = ReadJournal<CompletedEntry>(_completedLog);
        _state.Batches = ReadJournal<BatchRoot>(_batchLog);
        lock (_gate) Prune();
    }

    public static string Key(string peerId, string id) => peerId + "|" + id;

    public IReadOnlyList<OutboxEntry> PendingFor(string peerId)
    {
        lock (_gate) { Prune(); return _state.Outbox.Where(e => e.PeerId == peerId).ToList(); }
    }

    public void AddOutbox(OutboxEntry entry)
    {
        lock (_gate) { _state.Outbox.RemoveAll(e => e.TransferId == entry.TransferId); _state.Outbox.Add(entry); Save(); }
    }

    /// <summary>Queues several entries with one write (the rest of an interrupted folder).</summary>
    public void AddOutboxRange(IEnumerable<OutboxEntry> entries)
    {
        lock (_gate)
        {
            foreach (var entry in entries) { _state.Outbox.RemoveAll(e => e.TransferId == entry.TransferId); _state.Outbox.Add(entry); }
            Save();
        }
    }

    public void RemoveOutbox(string transferId)
    {
        lock (_gate) { if (_state.Outbox.RemoveAll(e => e.TransferId == transferId) > 0) Save(); }
    }

    /// <summary>Where this exact file (same id and hash) was stored, if we received it recently.</summary>
    public string? CompletedPath(string peerId, string transferId, string sha256)
    {
        lock (_gate)
            return _state.Completed.LastOrDefault(c => c.Key == Key(peerId, transferId) &&
                string.Equals(c.Sha256, sha256, StringComparison.OrdinalIgnoreCase))?.SavedPath;
    }

    public void MarkCompleted(string peerId, string transferId, string savedPath, string sha256)
    {
        lock (_gate)
        {
            var entry = new CompletedEntry(Key(peerId, transferId), savedPath, _time.GetUtcNow(), sha256);
            _state.Completed.Add(entry);
            if (_state.Completed.Count > MaxCompleted) _state.Completed.RemoveRange(0, _state.Completed.Count - MaxCompleted);
            Append(_completedLog, entry, _state.Completed);
        }
    }

    /// <summary>Returns the root folder name for a batch, choosing and remembering one with <paramref name="choose"/> on first use.</summary>
    public string BatchRootName(string peerId, string batchId, Func<string> choose)
    {
        lock (_gate)
        {
            var key = Key(peerId, batchId);
            var existing = _state.Batches.FirstOrDefault(b => b.Key == key);
            if (existing is not null) return existing.RootName;
            var name = choose();
            var entry = new BatchRoot(key, name, _time.GetUtcNow());
            _state.Batches.Add(entry);
            Append(_batchLog, entry, _state.Batches);
            return name;
        }
    }

    private void Prune()
    {
        var cutoff = _time.GetUtcNow() - Lifetime;
        if (_state.Outbox.RemoveAll(e => e.Created < cutoff) > 0) Save();
        _state.Completed.RemoveAll(c => c.At < cutoff); // journals are compacted as they grow
        _state.Batches.RemoveAll(b => b.At < cutoff);
    }

    /// <summary>Appends one JSON line; rewrites the journal from <paramref name="live"/> when it gets long.</summary>
    private void Append<T>(string? path, T entry, List<T> live)
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(entry) + "\n");
            if (new FileInfo(path).Length > 400 * MaxCompleted) // ~2× the live entries: compact
            {
                var temp = path + ".tmp";
                File.WriteAllLines(temp, live.Select(e => JsonSerializer.Serialize(e)));
                File.Move(temp, path, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private List<T> ReadJournal<T>(string? path)
    {
        var list = new List<T>();
        if (path is null || !File.Exists(path)) return list;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { if (JsonSerializer.Deserialize<T>(line) is { } e) list.Add(e); }
                catch (JsonException) { } // a torn last line after a crash: skip it
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list.Count > MaxCompleted ? list.GetRange(list.Count - MaxCompleted, MaxCompleted) : list;
    }

    private State Load()
    {
        if (_path is null || !File.Exists(_path)) return new State();
        try
        {
            return JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new State();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new State(); // unreadable: start clean rather than fail
        }
    }

    /// <summary>Marks the state dirty; it is written within <see cref="FlushDelay"/>.</summary>
    private void Save()
    {
        if (_path is null) return;
        _dirty = true;
        if (_flushScheduled) return;
        _flushScheduled = true;
        _flushTimer.Change(FlushDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Writes pending changes now (also called on <see cref="Dispose"/>).</summary>
    public void Flush()
    {
        if (_path is null) return;
        string json;
        lock (_gate)
        {
            _flushScheduled = false;
            if (!_dirty) return;
            _dirty = false;
            json = JsonSerializer.Serialize(new State { Outbox = _state.Outbox }); // completed/batches live in their journals
        }
        try
        {
            lock (_path) // one writer at a time; the state lock isn't held during disk I/O
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: resume after an app restart is lost, resume after a reconnect still works.
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        Flush();
    }
}
