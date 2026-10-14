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
    private readonly FileTransferService _fileTransfer;
    private readonly ClipboardSyncService _clipboard;
    private readonly TcpBridgeListener _listener;
    private readonly DiscoveryResponder? _discovery;
    private readonly BridgeHost _host;
    private readonly NotificationActionRouter _notificationActions;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherQueueTimer _qrTimer;
    private BridgeSession? _session;
    private readonly string _downloadDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Bridge");

    public MainWindow()
    {
        InitializeComponent();

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bridge");
        // Unfinished sends/receives survive reconnects and app restarts (24 h).
        _fileTransfer = new FileTransferService(new TransferStateStore(dataDir));
        var identity = DeviceIdentity.LoadOrCreate(Path.Combine(dataDir, "identity.bin"));
        var store = new JsonPairedDeviceStore(Path.Combine(dataDir, "paired_devices.json"));
        _pairing = new PairingService(identity, store, Environment.MachineName);
        _clipboard = new ClipboardSyncService(new WindowsClipboardAccess(DispatcherQueue));
        _listener = new TcpBridgeListener(); // fixed port so paired phones can find us again
        _host = new BridgeHost(identity, store, _pairing, _fileTransfer, _clipboard, _downloadDir);
        _notificationActions = new NotificationActionRouter(_host);

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
            var sending = progress.Direction == TransferDirection.Sending;
            var verb = sending ? "Sending" : "Receiving";
            // Folder transfers: "Holiday — file 12 of 340: IMG_0012.jpg"
            var name = progress.BatchCount is { } count && progress.BatchIndex is { } index
                ? $"{progress.RelativePath?.Split('/')[0]} — file {index + 1} of {count}: {progress.FileName}"
                : progress.FileName;
            switch (progress.Status)
            {
                case TransferStatus.InProgress:
                    TransferProgressBar.Visibility = Visibility.Visible;
                    TransferProgressBar.Value = progress.Fraction;
                    TransferText.Text = progress.ResumedFrom > 0 ? $"Resuming {name} ({progress.Fraction:P0})…" : $"{verb} {name}…";
                    break;
                case TransferStatus.Completed:
                    TransferProgressBar.Visibility = Visibility.Collapsed;
                    TransferText.Text = sending
                        ? $"Sent {name}"
                        : $"Saved {Path.GetFileName(progress.SavedPath ?? progress.FileName)} to Downloads\\Bridge";
                    break;
                case TransferStatus.Interrupted:
                    TransferProgressBar.Visibility = Visibility.Collapsed;
                    TransferText.Text = $"{name} paused at {progress.Fraction:P0} — it will continue when the phone reconnects.";
                    break;
                case TransferStatus.Failed:
                    TransferProgressBar.Visibility = Visibility.Collapsed;
                    TransferText.Text = $"{name} didn't transfer correctly. Please try again.";
                    break;
                case TransferStatus.Cancelled:
                    TransferProgressBar.Visibility = Visibility.Collapsed;
                    TransferText.Text = $"{name} was cancelled.";
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
                // Both report status via ProgressChanged and don't throw for I/O.
                if (item is global::Windows.Storage.StorageFile file)
                    await session.SendFileAsync(file.Path);
                else if (item is global::Windows.Storage.StorageFolder folder)
                    await session.SendFolderAsync(folder.Path);
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

    private void ShowPhoneNotification(NotificationPostPayload note)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("bridge", NotificationToastCommand.Open)
                .AddText(note.Title)
                .SetAttributionText(note.AppName);
            if (note.Text.Length > 0) builder.AddText(note.Text);
            // Windows allows 5 buttons. One reply box (Windows supports a single
            // inline text box well), the phone's own buttons, then Dismiss.
            var buttons = 0;
            var replied = false;
            foreach (var action in note.Actions ?? [])
            {
                if (buttons >= 4) break;
                if (action.Reply == true)
                {
                    if (replied) continue;
                    replied = true;
                    builder.AddTextBox(NotificationToastCommand.ReplyInputId, "Reply", "");
                    builder.AddButton(Button(action.Title.Length > 0 ? action.Title : "Send",
                        new NotificationToastCommand(NotificationToastCommand.Reply, note.Key, action.Id)).SetInputId(NotificationToastCommand.ReplyInputId));
                }
                else
                {
                    builder.AddButton(Button(action.Title, new NotificationToastCommand(NotificationToastCommand.Press, note.Key, action.Id)));
                }
                buttons++;
            }
            if (note.CanDismiss == true)
                builder.AddButton(Button("Dismiss", new NotificationToastCommand(NotificationToastCommand.Dismiss, note.Key)));
            if (note.AppId is not null)
                builder.AddButton(Button($"Stop {note.AppName} notifications",
                    new NotificationToastCommand(NotificationToastCommand.Mute, AppId: note.AppId)).SetContextMenuPlacement());

            var notification = builder.BuildNotification();
            notification.Tag = NotificationTag(note.Key);
            notification.Group = NotificationGroup;
            // The app's own update after a reply from here ("You: …"): update it quietly, don't pop up again.
            notification.SuppressDisplay = _notificationActions.TakeRecentAction(note.Key);
            AppNotificationManager.Default.Show(notification);
            lock (AppNames) { AppNames[note.Key] = note.AppName; if (AppNames.Count > 500) AppNames.Clear(); }
        }
        catch (Exception)
        {
            // Notifications unavailable (e.g. disabled for the app); mirroring is best effort.
        }
    }

    /// <summary>App name per notification key, for failure messages after a click.</summary>
    private static readonly Dictionary<string, string> AppNames = new();

    private static AppNotificationButton Button(string title, NotificationToastCommand command)
    {
        var button = new AppNotificationButton(title);
        foreach (var (k, v) in command.ToArguments()) button.AddArgument(k, v);
        return button;
    }

    /// <summary>A toast (or one of its buttons) was clicked; also used when Windows launched Bridge from a toast.</summary>
    public async void OnToastInvoked(IDictionary<string, string> arguments, IDictionary<string, string> userInput)
    {
        var command = NotificationToastCommand.Parse(arguments);
        if (command.Kind == NotificationToastCommand.Open)
        {
            DispatcherQueue.TryEnqueue(App.ShowMainWindow);
            return;
        }
        string? status;
        try { status = await command.RunAsync(_notificationActions, userInput, _cts.Token); }
        catch (OperationCanceledException) { return; }
        string appName = "the app";
        if (command.Key is not null) lock (AppNames) { if (AppNames.TryGetValue(command.Key, out var n)) appName = n; }
        if (command.FailureMessage(status, appName) is { } message) ShowInfo(message);
    }

    private static void ShowInfo(string message)
    {
        try { AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText("Bridge").AddText(message).BuildNotification()); }
        catch (Exception) { }
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
        _fileTransfer.State.Dispose(); // write any pending resume state before exiting
        await _host.DisposeAsync();
        _listener.Dispose();
    }
}
