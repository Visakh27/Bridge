using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;

namespace Bridge.Core;

/// <summary>
/// Orchestrates one live connection to a paired Android device: owns the
/// EncryptedChannel, dispatches incoming messages to FileTransferService /
/// ClipboardSyncService / notification + status handling, and runs the
/// presence ping/pong reconnect loop (section 18 counterpart on Windows).
/// This is the class Bridge.Windows' dashboard binds to.
/// </summary>
public sealed class BridgeSession : IAsyncDisposable
{
    private readonly EncryptedChannel _channel;
    private readonly FileTransferService _fileTransfer;
    private readonly ClipboardSyncService _clipboard;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private Task? _presenceLoop;

    public ConnectedDevice Device { get; }
    public event Action<string, string, string, string>? NotificationReceived; // appName, title, text, key
    public event Action<ConnectedDevice>? StatusChanged;

    public BridgeSession(EncryptedChannel channel, ConnectedDevice device, FileTransferService fileTransfer, ClipboardSyncService clipboard)
    {
        _channel = channel;
        Device = device;
        _fileTransfer = fileTransfer;
        _clipboard = clipboard;
        _clipboard.Channel = channel;
    }

    public void Start(string downloadDir)
    {
        _receiveLoop = ReceiveLoopAsync(downloadDir, _cts.Token);
        _presenceLoop = PresenceLoopAsync(_cts.Token);
    }

    public Task SendFileAsync(string path, CancellationToken ct = default) =>
        _fileTransfer.SendFileAsync(_channel, path, ct);

    private async Task ReceiveLoopAsync(string downloadDir, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _channel.IsConnected)
            {
                var message = await _channel.ReceiveAsync(ct);
                Device.LastSeen = DateTimeOffset.UtcNow;

                switch (message.Type)
                {
                    case MessageType.ClipboardUpdate:
                        await _clipboard.HandleIncomingAsync(message);
                        break;
                    case MessageType.FileStart:
                    case MessageType.FileChunk:
                    case MessageType.FileEnd:
                    case MessageType.FileCancel:
                        await _fileTransfer.HandleIncomingAsync(_channel, message, downloadDir, ct);
                        break;
                    case MessageType.DeviceStatus:
                        var status = message.PayloadAs<DeviceStatusPayload>();
                        Device.BatteryPercent = status.BatteryPercent;
                        Device.Charging = status.Charging;
                        Device.ConnectionQuality = status.ConnectionQuality;
                        StatusChanged?.Invoke(Device);
                        break;
                    case MessageType.NotificationPost:
                        var note = message.PayloadAs<NotificationPostPayload>();
                        NotificationReceived?.Invoke(note.AppName, note.Title, note.Text, note.Key);
                        break;
                    case MessageType.PresencePing:
                        await _channel.SendAsync(BridgeMessage.Create(MessageType.PresencePong, new { }), ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception)
        {
            Device.IsConnected = false;
            StatusChanged?.Invoke(Device);
        }
    }

    private async Task PresenceLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                await _channel.SendAsync(BridgeMessage.Create(MessageType.PresencePing, new { }), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_receiveLoop is not null) await SafeAwaitAsync(_receiveLoop);
        if (_presenceLoop is not null) await SafeAwaitAsync(_presenceLoop);
        await _channel.DisposeAsync();
    }

    private static async Task SafeAwaitAsync(Task t)
    {
        try { await t; } catch { /* ignore during shutdown */ }
    }
}
