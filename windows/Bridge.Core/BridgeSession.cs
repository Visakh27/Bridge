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
    private readonly string _downloadDir;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _pingInterval;
    private readonly TimeSpan _peerTimeout;
    private Task? _receiveLoop;
    private Task? _presenceLoop;
    private int _ended;

    public ConnectedDevice Device { get; }
    public event Action<NotificationPostPayload>? NotificationReceived;
    public event Action<string>? NotificationDismissed; // notification key
    public event Action<ConnectedDevice>? StatusChanged;
    public event Action<ErrorPayload>? PeerError;
    /// <summary>Raised once when the connection ends for any reason.</summary>
    public event Action<BridgeSession>? Ended;

    public BridgeSession(EncryptedChannel channel, ConnectedDevice device, FileTransferService fileTransfer,
        ClipboardSyncService clipboard, string downloadDir, TimeSpan? pingInterval = null, TimeSpan? peerTimeout = null)
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
    }

    public Task<TransferProgress> SendFileAsync(string path, CancellationToken ct = default) =>
        _fileTransfer.SendFileAsync(_channel, path, ct);

    public Task SendAsync(BridgeMessage message, CancellationToken ct = default) => _channel.SendAsync(message, ct);

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
                catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or IOException or UnauthorizedAccessException)
                {
                    // One malformed message (or a local disk error) shouldn't drop the connection.
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
                await _fileTransfer.HandleIncomingAsync(_channel, message, _downloadDir, ct);
                break;
            case MessageType.DeviceStatus:
                var status = message.PayloadAs<DeviceStatusPayload>();
                Device.BatteryPercent = Math.Clamp(status.BatteryPercent, -1, 100);
                Device.Charging = status.Charging;
                Device.ConnectionQuality = status.ConnectionQuality ?? "unknown";
                StatusChanged?.Invoke(Device);
                break;
            case MessageType.NotificationPost:
                NotificationReceived?.Invoke(message.PayloadAs<NotificationPostPayload>());
                break;
            case MessageType.NotificationDismiss:
                NotificationDismissed?.Invoke(message.PayloadAs<NotificationDismissPayload>().Key);
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
        _fileTransfer.AbortIncoming();
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
        await EndAsync();
    }

    private static async Task SafeAwaitAsync(Task t)
    {
        try { await t; } catch { /* ignore during shutdown */ }
    }
}
