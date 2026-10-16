using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Bridge.Network;
using Bridge.Protocol;

namespace Bridge.Core;

public sealed record PhotoPage(string Status, IReadOnlyList<PhotoInfo> Items, string? Next);
public sealed record PhotoThumb(string Status, byte[]? Jpeg);
public sealed record PhotoFetch(string Status, string? SavedPath = null, string? TransferId = null);

/// <summary>
/// The PC side of photo browsing: asks the connected phone for pages of photos
/// (newest first), small previews and originals. Answers are matched to
/// requests by id; previews are cached (and a preview already on its way is
/// shared, not asked for twice); an original arrives as a normal file transfer
/// that this waits for. Everything from the phone is checked before use.
/// </summary>
public sealed class PhotoBrowser
{
    public const int DefaultPageSize = 60;
    public const int ThumbCacheSize = 400;
    public const int MaxThumbBytes = 256 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DefaultFetchTimeout = TimeSpan.FromMinutes(10);

    private static readonly Regex IdPattern = new("^[0-9]{1,19}$", RegexOptions.CultureInvariant);
    private static readonly Regex CursorPattern = new("^[0-9]{1,15}:[0-9]{1,19}$", RegexOptions.CultureInvariant);

    private readonly BridgeHost _host;
    private readonly FileTransferService _files;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _fetchTimeout;
    private readonly ConcurrentDictionary<string, (BridgeSession Session, TaskCompletionSource<BridgeMessage?> Answer)> _pending = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BridgeSession, object> _subscribed = new();
    private readonly ConcurrentDictionary<string, Task<PhotoThumb>> _thumbsInFlight = new();
    private readonly LinkedList<(string Id, byte[] Jpeg)> _thumbLru = new();
    private readonly Dictionary<string, LinkedListNode<(string Id, byte[] Jpeg)>> _thumbIndex = new();

    public PhotoBrowser(BridgeHost host, FileTransferService files, TimeSpan? timeout = null, TimeSpan? fetchTimeout = null)
    {
        _host = host;
        _files = files;
        _timeout = timeout ?? DefaultTimeout;
        _fetchTimeout = fetchTimeout ?? DefaultFetchTimeout;
        host.SessionStarted += Subscribe;
        if (host.CurrentSession is { } live) Subscribe(live); // created while a phone is already connected
    }

    private void Subscribe(BridgeSession session)
    {
        lock (_subscribed)
        {
            if (_subscribed.TryGetValue(session, out _)) return;
            _subscribed.Add(session, new object());
        }
        session.PhotosMessage += OnPhotosMessage;
        // Only this session's requests end with it (a newer session's are unaffected).
        session.Ended += ended =>
        {
            foreach (var p in _pending.Values) if (ReferenceEquals(p.Session, ended)) p.Answer.TrySetResult(null);
        };
    }

    /// <summary>The phone's photos changed (a photo was taken or deleted): refresh.</summary>
    public event Action? Changed;

    /// <summary>Diagnostics: (kind, status) per request — never ids or names.</summary>
    public event Action<string, string>? Completed;

    /// <summary>Whether a connected phone offers photos at all (an older app doesn't).</summary>
    public bool PhoneSupportsPhotos => _host.CurrentSession is { IsRunning: true, SupportsPhotos: true };

    private void OnPhotosMessage(BridgeMessage message)
    {
        if (message.Type == MessageType.PhotosChanged) { Changed?.Invoke(); return; }
        string? requestId = null;
        try { requestId = message.Payload["requestId"]?.GetValue<string>(); }
        catch (InvalidOperationException) { } // not a string: not ours
        if (requestId is not null && _pending.TryGetValue(requestId, out var waiting)) waiting.Answer.TrySetResult(message);
    }

    /// <summary>Sends a request and waits for the answer with the same request id (null: no phone, dropped, or timed out).</summary>
    private async Task<BridgeMessage?> RequestAsync(string type, Func<string, object> payload, TimeSpan timeout, CancellationToken ct)
    {
        var session = _host.CurrentSession;
        if (session is not { IsRunning: true, SupportsPhotos: true }) return null;
        Subscribe(session);
        var requestId = Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<BridgeMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = (session, answer);
        try
        {
            await session.SendAsync(BridgeMessage.Create(type, payload(requestId)), ct);
            if (!session.IsRunning) return null; // ended between the check and the send
            return await answer.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException) { return null; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or System.Net.Sockets.SocketException) { return null; }
        finally { _pending.TryRemove(requestId, out _); }
    }

    private T Report<T>(string kind, string status, T result) { Completed?.Invoke(kind, status); return result; }

    /// <summary>A page of photos, newest first, older than <paramref name="before"/> (a cursor from the previous page).</summary>
    public async Task<PhotoPage> ListAsync(string? before = null, int limit = DefaultPageSize, CancellationToken ct = default)
    {
        if (before is not null && !CursorPattern.IsMatch(before)) return new(PhotoStatus.Invalid, [], null);
        limit = Math.Clamp(limit, 1, 100);
        var reply = await RequestAsync(MessageType.PhotosList, id => new PhotosListPayload(id, limit) { Before = before }, _timeout, ct);
        if (reply is null) return Report("list", PhotoStatus.Unavailable, new PhotoPage(PhotoStatus.Unavailable, [], null));
        PhotosPagePayload page;
        try { page = reply.PayloadAs<PhotosPagePayload>(); }
        catch (InvalidDataException) { return Report("list", PhotoStatus.Error, new PhotoPage(PhotoStatus.Error, [], null)); }
        var status = page.Status ?? PhotoStatus.Error;
        if (status != PhotoStatus.Ok) return Report("list", status, new PhotoPage(status, [], null));
        var items = (page.Items ?? []).Where(Valid).Take(limit).Select(Clean).ToList();
        var next = page.Next is { } n && CursorPattern.IsMatch(n) ? n : null;
        return Report("list", status, new PhotoPage(status, items, next));
    }

    private static bool Valid(PhotoInfo? p) =>
        p is not null && p.Id is not null && IdPattern.IsMatch(p.Id) && p.Width >= 0 && p.Height >= 0 && p.Size >= 0;

    private static PhotoInfo Clean(PhotoInfo p) => p with
    {
        Name = SafeFileName.Sanitize(PhotoName(p)),
        Mime = p.Mime is { Length: <= 64 } m && m.StartsWith("image/", StringComparison.Ordinal) ? m : "image/*",
    };

    private static string PhotoName(PhotoInfo p) => string.IsNullOrWhiteSpace(p.Name) ? $"photo_{p.Id}.jpg" : p.Name;

    /// <summary>A small JPEG preview (cached; at most one request per photo at a time).</summary>
    public Task<PhotoThumb> ThumbnailAsync(string id, CancellationToken ct = default)
    {
        if (id is null || !IdPattern.IsMatch(id)) return Task.FromResult(new PhotoThumb(PhotoStatus.Invalid, null));
        lock (_thumbLru)
        {
            if (_thumbIndex.TryGetValue(id, out var node))
            {
                _thumbLru.Remove(node);
                _thumbLru.AddFirst(node);
                return Task.FromResult(new PhotoThumb(PhotoStatus.Ok, node.Value.Jpeg));
            }
        }
        return _thumbsInFlight.GetOrAdd(id, _ => FetchThumbAsync(id, ct));
    }

    private async Task<PhotoThumb> FetchThumbAsync(string id, CancellationToken ct)
    {
        try
        {
            var reply = await RequestAsync(MessageType.PhotosThumb, r => new PhotosThumbPayload(r, id), _timeout, ct);
            if (reply is null) return Report("thumb", PhotoStatus.Unavailable, new PhotoThumb(PhotoStatus.Unavailable, null));
            PhotosThumbnailPayload t;
            try { t = reply.PayloadAs<PhotosThumbnailPayload>(); }
            catch (InvalidDataException) { return Report("thumb", PhotoStatus.Error, new PhotoThumb(PhotoStatus.Error, null)); }
            var status = t.Status ?? PhotoStatus.Error;
            if (status != PhotoStatus.Ok) return Report("thumb", status, new PhotoThumb(status, null));
            var jpeg = DecodeJpeg(t);
            if (jpeg is null || t.Id != id) return Report("thumb", PhotoStatus.Error, new PhotoThumb(PhotoStatus.Error, null));
            lock (_thumbLru)
            {
                if (!_thumbIndex.ContainsKey(id)) _thumbIndex[id] = _thumbLru.AddFirst((id, jpeg));
                while (_thumbLru.Count > ThumbCacheSize) { _thumbIndex.Remove(_thumbLru.Last!.Value.Id); _thumbLru.RemoveLast(); }
            }
            return Report("thumb", status, new PhotoThumb(PhotoStatus.Ok, jpeg));
        }
        finally
        {
            _thumbsInFlight.TryRemove(id, out _);
        }
    }

    /// <summary>Base64 JPEG within the size cap and starting with the JPEG marker, or null.</summary>
    private static byte[]? DecodeJpeg(PhotosThumbnailPayload t)
    {
        if (t.Mime != "image/jpeg" || t.Data is null || t.Data.Length > MaxThumbBytes * 4 / 3 + 4) return null;
        try
        {
            var bytes = Convert.FromBase64String(t.Data);
            return bytes.Length is > 3 and <= MaxThumbBytes && bytes[0] == 0xFF && bytes[1] == 0xD8 ? bytes : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>Clears cached previews (the phone's library changed, or another phone connected).</summary>
    public void ClearCache()
    {
        lock (_thumbLru) { _thumbLru.Clear(); _thumbIndex.Clear(); }
    }

    /// <summary>
    /// Asks the phone for the original of photo <paramref name="id"/> and waits for its file transfer.
    /// Completed: <see cref="PhotoFetch.SavedPath"/> is where it was saved. "interrupted": the connection
    /// dropped; it continues when the phone is back.
    /// </summary>
    public async Task<PhotoFetch> FetchAsync(string id, CancellationToken ct = default)
    {
        if (id is null || !IdPattern.IsMatch(id)) return new(PhotoStatus.Invalid);
        // Listen before asking: the transfer can finish before the answer is processed here.
        var outcomes = new ConcurrentDictionary<string, TransferProgress>();
        var wanted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource<TransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnProgress(TransferProgress p)
        {
            if (p.Direction != TransferDirection.Receiving || p.Status == TransferStatus.InProgress) return;
            outcomes[p.TransferId] = p;
            if (wanted.Task.IsCompleted && wanted.Task.Result == p.TransferId) done.TrySetResult(p);
        }
        _files.ProgressChanged += OnProgress;
        try
        {
            var reply = await RequestAsync(MessageType.PhotosFetch, r => new PhotosFetchPayload(r, id), _timeout, ct);
            if (reply is null) return Report("fetch", PhotoStatus.Unavailable, new PhotoFetch(PhotoStatus.Unavailable));
            PhotosFetchedPayload f;
            try { f = reply.PayloadAs<PhotosFetchedPayload>(); }
            catch (InvalidDataException) { return Report("fetch", PhotoStatus.Error, new PhotoFetch(PhotoStatus.Error)); }
            if (f.Status != PhotoStatus.Ok) return Report("fetch", f.Status ?? PhotoStatus.Error, new PhotoFetch(f.Status ?? PhotoStatus.Error));
            if (f.TransferId is not { Length: > 0 and <= 64 } transferId) return Report("fetch", PhotoStatus.Error, new PhotoFetch(PhotoStatus.Error));
            wanted.TrySetResult(transferId);
            if (outcomes.TryGetValue(transferId, out var early)) done.TrySetResult(early);
            TransferProgress result;
            try { result = await done.Task.WaitAsync(_fetchTimeout, ct); }
            catch (TimeoutException) { return Report("fetch", PhotoStatus.Unavailable, new PhotoFetch(PhotoStatus.Unavailable, TransferId: transferId)); }
            var outcome = result.Status switch
            {
                TransferStatus.Completed => new PhotoFetch(PhotoStatus.Ok, result.SavedPath, transferId),
                TransferStatus.Interrupted => new PhotoFetch("interrupted", TransferId: transferId),
                _ => new PhotoFetch(PhotoStatus.Error, TransferId: transferId),
            };
            return Report("fetch", outcome.Status, outcome);
        }
        finally
        {
            _files.ProgressChanged -= OnProgress;
        }
    }

    /// <summary>What to tell the user for a non-ok status (null = nothing).</summary>
    public static string? Explain(string status) => status switch
    {
        PhotoStatus.Ok => null,
        PhotoStatus.Disabled => "Turn on \"Let your PC browse photos\" in the Bridge app on your phone (Settings).",
        PhotoStatus.NoPermission => "Allow photo access for Bridge on your phone (Bridge › Settings › Allow photo access).",
        PhotoStatus.NotFound => "That photo is no longer on your phone.",
        PhotoStatus.Busy => "Your phone is busy. Try again in a moment.",
        PhotoStatus.Unavailable => "Connect your phone to see its photos.",
        "interrupted" => "The connection dropped. The photo will finish downloading when your phone reconnects.",
        _ => "Something went wrong getting that photo. Try again.",
    };
}
