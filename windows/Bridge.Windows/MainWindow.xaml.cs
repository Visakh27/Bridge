using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Bridge.Core;
using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Bridge.Windows;

/// <summary>
/// The main dashboard window. Wires Bridge.Core's BridgeHost to the WinUI
/// controls; intentionally thin — pairing, authentication and session logic
/// live in Bridge.Core / Bridge.Network (and are unit-tested there).
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string NotificationGroup = "bridge";

    private readonly PairingService _pairing;
    private readonly FileTransferService _fileTransfer = new();
    private readonly ClipboardSyncService _clipboard;
    private readonly TcpBridgeListener _listener;
    private readonly DiscoveryResponder? _discovery;
    private readonly BridgeHost _host;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherQueueTimer _qrTimer;
    private BridgeSession? _session;
    private readonly string _downloadDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Bridge");

    public MainWindow()
    {
        InitializeComponent();

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bridge");
        var identity = DeviceIdentity.LoadOrCreate(Path.Combine(dataDir, "identity.bin"));
        var store = new JsonPairedDeviceStore(Path.Combine(dataDir, "paired_devices.json"));
        _pairing = new PairingService(identity, store, Environment.MachineName);
        _clipboard = new ClipboardSyncService(new WindowsClipboardAccess(DispatcherQueue));
        _listener = new TcpBridgeListener(); // fixed port so paired phones can find us again
        _host = new BridgeHost(identity, store, _pairing, _fileTransfer, _clipboard, _downloadDir);

        _fileTransfer.ProgressChanged += OnTransferProgress;
        _host.DevicePaired += device => DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = $"Paired with {device.DeviceName} — connecting…";
            _ = ShowQrCodeAsync(); // the old code is spent
        });
        _host.SessionStarted += OnSessionStarted;
        _host.SessionEnded += OnSessionEnded;

        AppWindow.Closing += (sender, args) =>
        {
            if (App.IsExiting) return;
            args.Cancel = true; // keep running in the tray
            sender.Hide();
        };

        // Pairing codes expire after 5 minutes; refresh shortly before.
        _qrTimer = DispatcherQueue.CreateTimer();
        _qrTimer.Interval = PairingService.DefaultTokenLifetime - TimeSpan.FromSeconds(30);
        _qrTimer.Tick += (_, _) => { if (_session is null) _ = ShowQrCodeAsync(); };
        _qrTimer.Start();

        _ = ShowQrCodeAsync();
        _ = ShowPairedStatusAsync(store);
        _ = _host.RunAsync(_listener, _cts.Token);

        // Lets a paired phone find this PC again if its IP address or port changes.
        try
        {
            _discovery = new DiscoveryResponder(identity, () => _listener.Port);
            _ = _discovery.RunAsync(_cts.Token);
        }
        catch (System.Net.Sockets.SocketException)
        {
            // UDP 47821 is in use: phones can still connect at the address from the QR code.
            _discovery = null;
        }
    }

    private async Task ShowQrCodeAsync()
    {
        try
        {
            var (png, payload) = _pairing.GenerateQrCode(_listener.Port);
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(png))
            {
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            }
            QrImage.Source = bitmap;
            QrInfoText.Text = $"{payload.Host}:{payload.Port} · code valid for 5 minutes";
        }
        catch (Exception ex)
        {
            QrInfoText.Text = $"Couldn't create a pairing code ({ex.GetType().Name}).";
        }
    }

    private async Task ShowPairedStatusAsync(IPairedDeviceStore store)
    {
        var paired = await store.ListAsync();
        if (paired.Count > 0 && _session is null)
            StatusText.Text = $"Waiting for {paired[^1].DeviceName} to connect";
    }

    private void OnRegenerateQrClicked(object sender, RoutedEventArgs e) => _ = ShowQrCodeAsync();

    private void OnSessionStarted(BridgeSession session)
    {
        session.StatusChanged += OnDeviceStatusChanged;
        session.NotificationReceived += ShowPhoneNotification;
        session.NotificationDismissed += RemovePhoneNotification;
        DispatcherQueue.TryEnqueue(() =>
        {
            _session = session;
            _clipboard.Enabled = ClipboardToggle.IsOn;
            PairingPanel.Visibility = Visibility.Collapsed;
            DashboardPanel.Visibility = Visibility.Visible;
            DeviceNameText.Text = session.Device.DeviceName;
            StatusText.Text = "Connected";
            BatteryText.Text = "Battery unknown";
        });
    }

    private void OnSessionEnded(BridgeSession session)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_session, session)) return; // already replaced by a reconnect
            _session = null;
            StatusText.Text = $"{session.Device.DeviceName} disconnected — waiting for it to reconnect";
            DashboardPanel.Visibility = Visibility.Collapsed;
            PairingPanel.Visibility = Visibility.Visible;
            _ = ShowQrCodeAsync();
        });
    }

    private void OnDeviceStatusChanged(ConnectedDevice device)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            BatteryText.Text = device.BatteryPercent >= 0
                ? $"Battery {device.BatteryPercent}%{(device.Charging ? " (charging)" : "")}"
                : "Battery unknown";
        });
    }

    private void OnTransferProgress(TransferProgress progress)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var verb = progress.Direction == TransferDirection.Sending ? "Sending" : "Receiving";
            switch (progress.Status)
            {
                case TransferStatus.InProgress:
                    TransferProgressBar.Visibility = Visibility.Visible;
                    TransferProgressBar.Value = progress.Fraction;
                    TransferText.Text = $"{verb} {progress.FileName}…";
                    break;
                case TransferStatus.Completed:
                    TransferProgressBar.Visibility = Visibility.Collapsed;
                    TransferText.Text = progress.Direction == TransferDirection.Sending
                        ? $"Sent {progress.FileName}"
                        : $"Saved {Path.GetFileName(progress.SavedPath ?? progress.FileName)} to Downloads\\Bridge";
                    break;
                case TransferStatus.Failed:
                    TransferProgressBar.Visibility = Visibility.Collapsed;
                    TransferText.Text = $"{progress.FileName} didn't transfer correctly. Please try again.";
                    break;
                case TransferStatus.Cancelled:
                    TransferProgressBar.Visibility = Visibility.Collapsed;
                    TransferText.Text = $"{progress.FileName} was cancelled.";
                    break;
            }
        });
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // "global::" because inside namespace Bridge.Windows a bare "Windows."
        // would resolve to this app's own namespace.
        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        try
        {
            var session = _session;
            if (session is null || !e.DataView.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                return;

            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (item is global::Windows.Storage.StorageFile file)
                    await session.SendFileAsync(file.Path); // reports status via ProgressChanged, doesn't throw for I/O
            }
        }
        catch (Exception)
        {
            TransferText.Text = "Couldn't send that file. Is the phone still connected?";
        }
    }

    private void OnClipboardToggled(object sender, RoutedEventArgs e) => _clipboard.Enabled = ClipboardToggle.IsOn;

    private void OnOpenReceivedClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_downloadDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_downloadDir}\"") { UseShellExecute = true });
    }

    private async void OnForgetClicked(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;
        try
        {
            await _host.RevokeAsync(session.Device.DeviceId);
            StatusText.Text = $"Forgot {session.Device.DeviceName}";
        }
        catch (Exception)
        {
            StatusText.Text = "Couldn't forget this phone. Please try again.";
        }
    }

    private static string NotificationTag(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]; // tags are limited to 64 chars

    private static void ShowPhoneNotification(NotificationPostPayload note)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(note.Title)
                .AddText(note.Text)
                .SetAttributionText(note.AppName)
                .BuildNotification();
            notification.Tag = NotificationTag(note.Key);
            notification.Group = NotificationGroup;
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception)
        {
            // Notifications unavailable (e.g. disabled for the app); mirroring is best effort.
        }
    }

    private static async void RemovePhoneNotification(string key)
    {
        try { await AppNotificationManager.Default.RemoveByTagAndGroupAsync(NotificationTag(key), NotificationGroup); }
        catch (Exception) { }
    }

    public async Task ShutdownAsync()
    {
        _qrTimer.Stop();
        _cts.Cancel();
        _discovery?.Dispose();
        await _host.DisposeAsync();
        _listener.Dispose();
    }
}
