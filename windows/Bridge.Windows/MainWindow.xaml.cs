using System.IO;
using Bridge.Core;
using Bridge.Network;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Bridge.Security;

namespace Bridge.Windows;

/// <summary>
/// The main dashboard window. Wires the Bridge.Core/Network layers to the
/// WinUI XAML controls; this is intentionally thin — all protocol logic
/// lives in Bridge.Core.BridgeSession.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly DeviceIdentity _identity;
    private readonly PairingService _pairing;
    private readonly FileTransferService _fileTransfer = new();
    private readonly TcpBridgeListener _listener = new();
    private BridgeSession? _session;
    private readonly string _downloadDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Bridge", "Received");

    public MainWindow()
    {
        InitializeComponent();

        var identityPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bridge", "identity.bin");
        _identity = DeviceIdentity.LoadOrCreate(identityPath);
        _pairing = new PairingService(_identity, new JsonPairedDeviceStore());

        _fileTransfer.ProgressChanged += OnTransferProgress;

        ShowQrCode();
        _ = AcceptIncomingConnectionsAsync();
    }

    private void ShowQrCode()
    {
        var (pngDataUri, _) = _pairing.GenerateQrCode(_listener.Port, Environment.MachineName);
        var bytes = Convert.FromBase64String(pngDataUri.Split(',')[1]);
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        // BitmapImage.SetSourceAsync requires an IRandomAccessStream; in a full
        // implementation wrap `stream` via System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream().
        QrImage.Source = bitmap;
    }

    private void OnRegenerateQrClicked(object sender, RoutedEventArgs e) => ShowQrCode();

    private async Task AcceptIncomingConnectionsAsync()
    {
        while (true)
        {
            var transport = await _listener.AcceptAsync();
            var channel = await EncryptedChannel.HandshakeAsResponderAsync(transport, _identity.DeviceId);
            var first = await channel.ReceiveAsync();

            if (first.Type == Bridge.Protocol.MessageType.PairingRequest)
            {
                var paired = await _pairing.HandlePairingRequestAsync(channel, first);
                if (paired is null) { await channel.DisposeAsync(); continue; }

                var device = new ConnectedDevice { DeviceId = paired.DeviceId, DeviceName = paired.DeviceName, IsConnected = true };
                var clipboard = new ClipboardSyncService(new WindowsClipboardAccess());
                _session = new BridgeSession(channel, device, _fileTransfer, clipboard);
                _session.StatusChanged += OnDeviceStatusChanged;
                _session.Start(_downloadDir);

                DispatcherQueue.TryEnqueue(() =>
                {
                    PairingPanel.Visibility = Visibility.Collapsed;
                    DashboardPanel.Visibility = Visibility.Visible;
                    DeviceNameText.Text = device.DeviceName;
                    StatusText.Text = "Connected";
                });
            }
        }
    }

    private void OnDeviceStatusChanged(ConnectedDevice device)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            BatteryText.Text = device.BatteryPercent >= 0
                ? $"Battery {device.BatteryPercent}%{(device.Charging ? " (charging)" : "")}"
                : "Battery unknown";
            StatusText.Text = device.IsConnected ? "Connected" : "Reconnecting…";
        });
    }

    private void OnTransferProgress(TransferProgress progress)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TransferProgressBar.Visibility = Visibility.Visible;
            TransferProgressBar.Value = progress.Fraction;
            if (progress.Status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled)
                TransferProgressBar.Visibility = Visibility.Collapsed;
        });
    }

    private void OnDragOver(object sender, DragEventArgs e) =>
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (_session is null || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is Windows.Storage.StorageFile file)
                await _session.SendFileAsync(file.Path);
        }
    }

    private void OnClipboardToggled(object sender, RoutedEventArgs e)
    {
        // ClipboardSyncService.Enabled is set on the active session's clipboard
        // service in a full implementation; wire up via _session when non-null.
    }
}
