using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Bridge.Network;
using Bridge.Protocol;

namespace Bridge.Core;

/// <summary>
/// Orchestrates one live, authenticated connection to a paired Android device:
/// owns the EncryptedChannel, dispatches incoming messages to
/// FileTransferService / ClipboardSyncService / notification + status
/// handling, and runs the presence ping + dead-peer watchdog. Created by
/// <see cref="BridgeHost"/>; this is what Bridge.Windows' dashboard binds to.
/// </summary>
public sealed class BridgeSession : IAsyncDisposable
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(45);

    private readonly EncryptedChannel _channel;
    private readonly FileTransferService _fileTransfer;
    private readonly ClipboardSyncService _clipboard;
    private readonly Func<string> _downloadDir;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _pingInterval;
    private readonly TimeSpan _peerTimeout;
    private Task? _receiveLoop;
    private Task? _presenceLoop;
    private Task? _resumeLoop;
    private int _ended;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingActions = new();

    /// <summary>How long to wait for the phone to answer a notification action.</summary>
    public static readonly TimeSpan NotificationActionTimeout = TimeSpan.FromSeconds(15);

    public ConnectedDevice Device { get; }
    public event Action<NotificationPostPayload>? NotificationReceived;
    public event Action<string>? NotificationDismissed; // notification key
    /// <summary>photos.page / photos.thumbnail / photos.fetched / photos.changed from the phone (see <see cref="PhotoBrowser"/>).</summary>
    public event Action<BridgeMessage>? PhotosMessage;
    /// <summary>handoff.open / handoff.ack from the phone (see <see cref="HandoffService"/>).</summary>
    public event Action<BridgeMessage>? HandoffMessage;
    public event Action<ConnectedDevice>? StatusChanged;
    public event Action<ErrorPayload>? PeerError;
    /// <summary>Diagnostics: a handler threw for a message of this type (payload contents are never included).</summary>
    public event Action<string, Exception>? HandlerFailed;
    /// <summary>Raised once when the connection ends for any reason.</summary>
    public event Action<BridgeSession>? Ended;

    public BridgeSession(EncryptedChannel channel, ConnectedDevice device, FileTransferService fileTransfer,
        ClipboardSyncService clipboard, string downloadDir, TimeSpan? pingInterval = null, TimeSpan? peerTimeout = null)
        : this(channel, device, fileTransfer, clipboard, () => downloadDir, pingInterval, peerTimeout) { }

    /// <param name="downloadDir">Read for each incoming file, so a changed download folder applies at once.</param>
    public BridgeSession(EncryptedChannel channel, ConnectedDevice device, FileTransferService fileTransfer,
        ClipboardSyncService clipboard, Func<string> downloadDir, TimeSpan? pingInterval = null, TimeSpan? peerTimeout = null)
    {
        _channel = channel;
        Device = device;
        _fileTransfer = fileTransfer;
        _clipboard = clipboard;
        _downloadDir = downloadDir;
        _pingInterval = pingInterval ?? PingInterval;
        _peerTimeout = peerTimeout ?? PeerTimeout;
    }

    public bool IsRunning => Volatile.Read(ref _ended) == 0;

    public void Start()
    {
        Device.IsConnected = true;
        Device.LastSeen = DateTimeOffset.UtcNow;
        _clipboard.PeerSupportsClipboard = Device.PeerFeatures.Contains(Features.Clipboard);
        _clipboard.Channel = _channel;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _presenceLoop = Task.Run(() => PresenceLoopAsync(_cts.Token));
        StatusChanged?.Invoke(Device);

        // Pick up where interrupted transfers left off (and tidy partials nobody came back for).
        FileTransferService.CleanupExpiredPartials(_downloadDir());
        _resumeLoop = Task.Run(async () =>
        {
            try { await _fileTransfer.ResumePendingAsync(_channel, SendOptions, _cts.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        });
    }

    /// <summary>What the connected phone supports, for outgoing transfers.</summary>
    public SendOptions SendOptions => new(Device.DeviceId,
        Device.PeerFeatures.Contains(Features.FilesResume), Device.PeerFeatures.Contains(Features.FilesFolders));

    public Task<TransferProgress> SendFileAsync(string path, CancellationToken ct = default) =>
        _fileTransfer.SendFileAsync(_channel, path, SendOptions, ct);

    /// <summary>Sends a whole folder (structure kept when the phone supports folders).</summary>
    public Task<IReadOnlyList<TransferProgress>> SendFolderAsync(string path, CancellationToken ct = default) =>
        _fileTransfer.SendFolderAsync(_channel, path, SendOptions, ct);

    public Task SendAsync(BridgeMessage message, CancellationToken ct = default) => _channel.SendAsync(message, ct);

    /// <summary>The phone lets this PC browse its photos (it may still answer "disabled" until the user turns it on).</summary>
    public bool SupportsPhotos => Device.PeerFeatures.Contains(Features.Photos);

    /// <summary>The phone can carry out replies, button presses, dismissals and mutes.</summary>
    public bool SupportsNotificationActions => Device.PeerFeatures.Contains(Features.NotificationsActions);

    /// <summary>
    /// Presses button <paramref name="actionId"/> of a phone notification (with
    /// <paramref name="replyText"/> for reply buttons) and waits for the phone's answer.
    /// Returns a <see cref="NotificationActionStatus"/>; <c>unavailable</c> if the phone doesn't
    /// support actions, the connection ends, or there's no answer in time.
    /// </summary>
    public async Task<string> InvokeNotificationActionAsync(string key, string actionId, string? replyText,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!SupportsNotificationActions || !IsRunning) return NotificationActionStatus.Unavailable;
        if (replyText is not null && (string.IsNullOrWhiteSpace(replyText) || replyText.Length > PhoneNotificationText.MaxReply))
            return NotificationActionStatus.Invalid;
        var requestId = Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingActions[requestId] = answer;
        try
        {
            await _channel.SendAsync(BridgeMessage.Create(MessageType.NotificationAction,
                new NotificationActionPayload(requestId, key, actionId) { ReplyText = replyText }), ct);
            if (!IsRunning) return NotificationActionStatus.Unavailable;
            return await answer.Task.WaitAsync(timeout ?? NotificationActionTimeout, ct);
        }
        catch (TimeoutException) { return NotificationActionStatus.Unavailable; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            return NotificationActionStatus.Unavailable;
        }
        finally
        {
            _pendingActions.TryRemove(requestId, out _);
        }
    }

    /// <summary>Clears a notification on the phone. False if the phone doesn't support it or isn't reachable.</summary>
    public Task<bool> DismissOnPhoneAsync(string key, CancellationToken ct = default) =>
        SendIfSupportedAsync(BridgeMessage.Create(MessageType.NotificationDismiss, new NotificationDismissPayload(key)), ct);

    /// <summary>Stops the phone mirroring an app's notifications (undone on the phone).</summary>
    public Task<bool> MuteAppOnPhoneAsync(string appId, CancellationToken ct = default) =>
        SendIfSupportedAsync(BridgeMessage.Create(MessageType.NotificationMute, new NotificationMutePayload(appId)), ct);

    private async Task<bool> SendIfSupportedAsync(BridgeMessage message, CancellationToken ct)
    {
        if (!SupportsNotificationActions || !IsRunning) return false;
        try { await _channel.SendAsync(message, ct); return true; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException or InvalidOperationException) { return false; }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await _channel.ReceiveAsync(ct); // transport/crypto failures end the session
                Device.LastSeen = DateTimeOffset.UtcNow;
                try
                {
                    await DispatchAsync(message, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A bad message or a failing local handler (malformed payload, disk
                    // error, the Windows clipboard being locked by another app…) must not
                    // drop the connection. If the connection itself broke, the next
                    // ReceiveAsync fails and ends the session.
                    HandlerFailed?.Invoke(message.Type, ex);
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex) when (ex is IOException or CryptographicException or InvalidDataException or JsonException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            // Peer went away or sent something we can't authenticate.
        }
        finally
        {
            await EndAsync();
        }
    }

    private async Task DispatchAsync(BridgeMessage message, CancellationToken ct)
    {
        switch (message.Type)
        {
            case MessageType.ClipboardUpdate:
                await _clipboard.HandleIncomingAsync(message);
                break;
            case MessageType.FileStart:
            case MessageType.FileChunk:
            case MessageType.FileEnd:
            case MessageType.FileCancel:
            case MessageType.FileAck:
            case MessageType.FileResume:
                await _fileTransfer.HandleIncomingAsync(_channel, message, _downloadDir(), ct);
                break;
            case MessageType.DeviceStatus:
                var status = message.PayloadAs<DeviceStatusPayload>();
                Device.BatteryPercent = Math.Clamp(status.BatteryPercent, -1, 100);
                Device.Charging = status.Charging;
                Device.ConnectionQuality = status.ConnectionQuality ?? "unknown";
                StatusChanged?.Invoke(Device);
                break;
            case MessageType.NotificationPost:
                if (PhoneNotificationText.Clean(message.PayloadAs<NotificationPostPayload>()) is { } note)
                    NotificationReceived?.Invoke(SupportsNotificationActions ? note : note with { Actions = null, CanDismiss = null, AppId = null });
                break;
            case MessageType.NotificationDismiss:
                if (message.PayloadAs<NotificationDismissPayload>().Key is { Length: > 0 } dismissedKey)
                    NotificationDismissed?.Invoke(dismissedKey);
                break;
            case MessageType.HandoffOpen:
            case MessageType.HandoffAck:
                HandoffMessage?.Invoke(message);
                break;
            case MessageType.PhotosPage:
            case MessageType.PhotosThumbnail:
            case MessageType.PhotosFetched:
            case MessageType.PhotosChanged:
                PhotosMessage?.Invoke(message);
                break;
            case MessageType.NotificationActionResult:
                var result = message.PayloadAs<NotificationActionResultPayload>();
                if (result.RequestId is not null && _pendingActions.TryGetValue(result.RequestId, out var waiting))
                    waiting.TrySetResult(result.Status ?? NotificationActionStatus.Failed);
                break;
            case MessageType.CapabilitiesAdvertise:
                Device.PeerFeatures = message.PayloadAs<CapabilitiesPayload>().Features ?? [];
                _clipboard.PeerSupportsClipboard = Device.PeerFeatures.Contains(Features.Clipboard);
                StatusChanged?.Invoke(Device);
                break;
            case MessageType.PresencePing:
                await _channel.SendAsync(BridgeMessage.CreateEmpty(MessageType.PresencePong), ct);
                break;
            case MessageType.PresencePong:
                break; // LastSeen already updated
            case MessageType.Error:
                PeerError?.Invoke(message.PayloadAs<ErrorPayload>());
                break;
            // Unknown types from a newer peer are ignored (protocol.md, Versioning).
        }
    }

    private async Task PresenceLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_pingInterval, ct);
                if (DateTimeOffset.UtcNow - Device.LastSeen > _peerTimeout)
                {
                    // Half-open TCP (phone left Wi-Fi): close so the receive loop ends.
                    await _channel.DisposeAsync();
                    return;
                }
                await _channel.SendAsync(BridgeMessage.CreateEmpty(MessageType.PresencePing), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            await _channel.DisposeAsync(); // send failed: connection is gone
        }
    }

    private async Task EndAsync()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        _cts.Cancel();
        Device.IsConnected = false;
        if (ReferenceEquals(_clipboard.Channel, _channel)) _clipboard.Channel = null;
        foreach (var waiting in _pendingActions.Values) waiting.TrySetResult(NotificationActionStatus.Unavailable);
        _fileTransfer.AbortAll(_channel);
        await _channel.DisposeAsync();
        StatusChanged?.Invoke(Device);
        Ended?.Invoke(this);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _channel.DisposeAsync(); // unblocks a pending receive
        if (_receiveLoop is not null) await SafeAwaitAsync(_receiveLoop);
        if (_presenceLoop is not null) await SafeAwaitAsync(_presenceLoop);
        if (_resumeLoop is not null) await SafeAwaitAsync(_resumeLoop);
        await EndAsync();
    }

    private static async Task SafeAwaitAsync(Task t)
    {
        try { await t; } catch { /* ignore during shutdown */ }
    }
}
