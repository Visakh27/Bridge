using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Bridge.Core;
using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    /// <summary>The active phone's session (what drops, Retry and Photos use); null when no phone is connected.</summary>
    private BridgeSession? _session;
    private IPairedDeviceStore _store = null!;
    /// <summary>The QR code is showing to pair another phone while one is connected.</summary>
    private bool _pairingAnother;
    private bool _fillingPhoneSelector;
    private readonly SettingsStore _settings;
    private readonly DiagnosticsLog _diagnostics;
    private readonly TransferHistory _history = new();
    private readonly PhotoBrowser _photos;
    private readonly ExplorerSendQueue _explorerSend;
    private readonly HandoffService _handoff;
    private readonly WindowsClipboardAccess _clipboardAccess;
    private const string HandoffToastGroup = "bridge-handoff";
    private const string SendToastTag = "explorer-send";
    private readonly System.Collections.ObjectModel.ObservableCollection<PhotoTile> _photoTiles = [];
    private string? _photoCursor;
    private bool _photosStale = true;
    private int _photoGeneration; // bumps on every reload so late answers for an old list are ignored
    private readonly string _dataDir;
    private bool _applyingSettings;
    private static readonly string DefaultDownloadDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Bridge");

    private string _downloadDir => _settings.Current.DownloadFolder ?? DefaultDownloadDir;

    public static string AppVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public MainWindow()
    {
        InitializeComponent();
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Bridge.ico")); } catch (Exception) { }

        var dataDir = _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bridge");
        _settings = new SettingsStore(Path.Combine(dataDir, "settings.json"));
        _diagnostics = new DiagnosticsLog(Path.Combine(dataDir, "logs")) { Enabled = _settings.Current.Diagnostics };
        // Unfinished sends/receives survive reconnects and app restarts (24 h).
        _fileTransfer = new FileTransferService(new TransferStateStore(dataDir));
        var identity = DeviceIdentity.LoadOrCreate(Path.Combine(dataDir, "identity.bin"));
        var store = new JsonPairedDeviceStore(Path.Combine(dataDir, "paired_devices.json"));
        _store = store;
        _pairing = new PairingService(identity, store, Environment.MachineName);
        _clipboardAccess = new WindowsClipboardAccess(DispatcherQueue);
        _clipboard = new ClipboardSyncService(_clipboardAccess);
        _listener = new TcpBridgeListener(); // fixed port so paired phones can find us again
        _host = new BridgeHost(identity, store, _pairing, _fileTransfer, _clipboard, _downloadDir);
        _notificationActions = new NotificationActionRouter(_host);
        _photos = new PhotoBrowser(_host, _fileTransfer);
        _photos.Changed += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (PhotosPanel.Visibility == Visibility.Visible) _ = ReloadPhotosAsync(); else _photosStale = true;
        });
        PhotosGrid.ItemsSource = _photoTiles;
        _explorerSend = new ExplorerSendQueue(_host);
        _explorerSend.Queued += (_, waiting) =>
        {
            _diagnostics.Info("explorer.queued", LogField.Num("items", waiting));
            ShowSendToast($"Your phone isn't connected. Bridge will send {Items(waiting)} when it reconnects (within {(int)_explorerSend.Wait.TotalMinutes} minutes).", cancel: true);
        };
        _explorerSend.Started += (request, phone) => ShowSendToast($"Sending {Items(request.Count)} to {phone}…");
        _explorerSend.Finished += summary =>
        {
            _diagnostics.Info("explorer.sent", LogField.Num("total", summary.Total), LogField.Num("sent", summary.Sent),
                LogField.Num("failed", summary.Failed), LogField.Num("interrupted", summary.Interrupted), LogField.Flag("queued", summary.WasQueued));
            ShowSendToast(ExplorerSendQueue.Describe(summary));
        };
        _explorerSend.Expired += request =>
        {
            _diagnostics.Info("explorer.expired", LogField.Num("items", request.Count));
            ShowSendToast($"Your phone didn't reconnect, so {Items(request.Count)} weren't sent. Send them again from File Explorer.");
        };
        _handoff = new HandoffService(_host, () => _settings.Current.ReceiveHandoff, () => _settings.Current.OpenLinksAutomatically);
        _handoff.LinkReceived += (link, open, phone) =>
        {
            if (open) OpenLink(link.Url);
            else ShowLinkToast(link, phone);
        };
        _handoff.TextReceived += (text, phone) => _ = Task.Run(async () =>
        {
            try
            {
                await _clipboard.WriteFromPeerAsync(text);
                ShowHandoffToast($"Copied from {phone}", HandoffRules.Preview(text));
            }
            catch (Exception) { ShowHandoffToast($"Text from {phone}", "Couldn't put it on the clipboard (another app is using it). Try again from your phone."); }
        });
        _ = new BridgeDiagnostics(_diagnostics, _host, _fileTransfer, _notificationActions, _photos, _handoff);
        _diagnostics.Info("app.started", LogField.Version("version", typeof(MainWindow).Assembly.GetName().Version),
            LogField.Version("os", Environment.OSVersion.Version), LogField.Flag("packaged", StartupRegistration.IsPackaged));

        _fileTransfer.ProgressChanged += OnTransferProgress;
        _history.Changed += QueueRenderRecent;
        ApplySettingsToUi();
        ShowOnboardingIfNeeded();
        // Send to: added once the first-run guide is done, refreshed each start (the app may have moved).
        if (_settings.Current.OnboardingDone && _settings.Current.SendToMenu) _ = Task.Run(() => ExplorerIntegration.SetSendTo(true));
        // Unpackaged: re-point the sign-in entry at this copy of Bridge (it may have been moved or updated).
        if (_settings.Current.StartWithWindows && !StartupRegistration.IsPackaged) _ = StartupRegistration.SetAsync(true);
        _host.DevicePaired += device => DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = $"Paired with {device.DeviceName} — connecting…";
            _pairingAnother = false; // back to the phones once it connects
            _ = ShowQrCodeAsync(); // the old code is spent
            _ = RenderPairedPhonesAsync();
        });
        _host.SessionStarted += OnSessionStarted;
        _host.SessionEnded += OnSessionEnded;
        _host.ActiveSessionChanged += _ => DispatcherQueue.TryEnqueue(RefreshPhones);

        AppWindow.Closing += (sender, args) =>
        {
            if (App.IsExiting) return;
            args.Cancel = true; // keep running in the tray
            sender.Hide();
        };

        // Pairing codes expire after 5 minutes; refresh shortly before.
        _qrTimer = DispatcherQueue.CreateTimer();
        _qrTimer.Interval = PairingService.DefaultTokenLifetime - TimeSpan.FromSeconds(30);
        _qrTimer.Tick += (_, _) => { if (PairingPanel.Visibility == Visibility.Visible) _ = ShowQrCodeAsync(); };
        _qrTimer.Start();

        _ = ShowQrCodeAsync();
        _ = ShowPairedStatusAsync(store);
        _ = _host.RunAsync(_listener, _cts.Token);

        // Lets a paired phone find this PC again if its IP address or port changes.
        try
        {
            _discovery = new DiscoveryResponder(identity, () => _listener.Port);
            _discovery.Answered += () => _diagnostics.Info("discovery.answered");
            _ = _discovery.RunAsync(_cts.Token);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            // UDP 47821 is in use: phones can still connect at the address from the QR code.
            _discovery = null;
            _diagnostics.Warn("discovery.unavailable", LogField.Error(ex));
        }
    }

    /// <summary>Whether to stay in the tray at launch (never before the first-run guide was seen).</summary>
    public bool ShouldStartHidden(bool launchedAtSignIn) =>
        _settings.Current.OnboardingDone && (launchedAtSignIn || _settings.Current.StartMinimized);

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
            QrInfoText.Text = "Couldn't create a pairing code. Select \"New code\" to try again.";
            _diagnostics.Warn("qr.failed", LogField.Error(ex));
        }
    }

    private async Task ShowPairedStatusAsync(IPairedDeviceStore store)
    {
        var paired = await store.ListAsync();
        if (paired.Count > 0 && _session is null)
            StatusText.Text = paired.Count == 1 ? $"Waiting for {paired[0].DeviceName} to connect" : $"Waiting for your {paired.Count} phones to connect";
    }

    private void OnRegenerateQrClicked(object sender, RoutedEventArgs e) => _ = ShowQrCodeAsync();

    private void OnSessionStarted(BridgeSession session)
    {
        session.StatusChanged += OnDeviceStatusChanged;
        session.NotificationReceived += note => ShowPhoneNotification(session.Device, note);
        session.NotificationDismissed += key => RemovePhoneNotification(session.Device.DeviceId, key);
        DispatcherQueue.TryEnqueue(() =>
        {
            _clipboard.Enabled = ClipboardToggle.IsOn;
            RefreshPhones();
            _ = RenderPairedPhonesAsync();
        });
    }

    private void OnSessionEnded(BridgeSession session)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshPhones();
            _ = RenderPairedPhonesAsync();
            if (_host.Sessions.Count == 0)
                StatusText.Text = $"{session.Device.DeviceName} disconnected — waiting for it to reconnect";
        });
    }

    /// <summary>Shows the connected phones and the active one. Runs on the UI thread after any change.</summary>
    private void RefreshPhones()
    {
        var sessions = _host.Sessions.Where(s => s.IsRunning).ToList();
        var active = _host.CurrentSession is { IsRunning: true } a ? a : null;
        if (!ReferenceEquals(_session, active))
        {
            _photosStale = true; // previews are cached per phone; the list is not
            if (PhotosPanel.Visibility == Visibility.Visible) _ = ReloadPhotosAsync();
        }
        _session = active;

        _fillingPhoneSelector = true;
        try
        {
            PhoneSelector.Items.Clear();
            foreach (var s in sessions.OrderBy(s => s.Device.DeviceName, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new ComboBoxItem { Content = s.Device.DeviceName, Tag = s.Device.DeviceId };
                PhoneSelector.Items.Add(item);
                if (ReferenceEquals(s, active)) PhoneSelector.SelectedItem = item;
            }
            PhoneSelector.Visibility = sessions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _fillingPhoneSelector = false; }

        var showDevices = active is not null && !_pairingAnother;
        // Back to the pairing code after the last phone left: show a fresh one (the old one may be spent).
        if (!showDevices && DevicePanel.Visibility == Visibility.Visible) _ = ShowQrCodeAsync();
        DevicePanel.Visibility = showDevices ? Visibility.Visible : Visibility.Collapsed;
        PairingPanel.Visibility = showDevices ? Visibility.Collapsed : Visibility.Visible;
        PairingBackButton.Visibility = active is not null && _pairingAnother ? Visibility.Visible : Visibility.Collapsed;
        PairingTitleText.Text = active is not null ? "Scan with the Bridge app on the other phone" : "Scan with the Bridge Android app";
        if (active is not null)
        {
            DeviceNameText.Text = active.Device.DeviceName;
            ShowBattery(active.Device);
            StatusText.Text = sessions.Count switch
            {
                1 => "Connected",
                2 => $"Connected to {sessions[0].Device.DeviceName} and {sessions[1].Device.DeviceName}",
                _ => $"{sessions.Count} phones connected",
            };
            DropZoneText.Text = sessions.Count > 1
                ? $"Drop files or folders here to send them to {active.Device.DeviceName}"
                : "Drop files or folders here to send them to your phone";
        }
        else
        {
            DropZoneText.Text = "Connect your phone to send files";
        }
        App.SetTrayToolTip(sessions.Count switch
        {
            0 => "Bridge — no phone connected",
            1 => $"Bridge — {sessions[0].Device.DeviceName}",
            _ => $"Bridge — {sessions.Count} phones connected",
        });
        RenderRecent();
    }

    private void OnPhoneSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingPhoneSelector || PhoneSelector.SelectedItem is not ComboBoxItem { Tag: string deviceId }) return;
        _host.ActiveDeviceId = deviceId; // raises ActiveSessionChanged → RefreshPhones
    }

    private void OnPairAnotherClicked(object sender, RoutedEventArgs e)
    {
        _pairingAnother = true;
        RefreshPhones(); // switching to the pairing view shows a fresh code
    }

    private void OnPairingBackClicked(object sender, RoutedEventArgs e)
    {
        _pairingAnother = false;
        RefreshPhones();
    }

    private void OnDeviceStatusChanged(ConnectedDevice device)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_session?.Device.DeviceId == device.DeviceId) ShowBattery(device);
        });
    }

    private void ShowBattery(ConnectedDevice device) =>
        BatteryText.Text = device.BatteryPercent >= 0
            ? $"Battery {device.BatteryPercent}%{(device.Charging ? " (charging)" : "")}"
            : "Battery unknown";

    /// <summary>Settings › Paired phones: every paired phone, whether it's connected, and Forget.</summary>
    private async Task RenderPairedPhonesAsync()
    {
        IReadOnlyList<PairedDevice> paired;
        try { paired = await _store.ListAsync(); }
        catch (Exception) { return; }
        PairedPhonesPanel.Children.Clear();
        if (paired.Count == 0)
        {
            PairedPhonesPanel.Children.Add(new TextBlock { Text = "No phones paired yet.", Opacity = 0.6 });
            return;
        }
        foreach (var device in paired.OrderBy(d => d.DeviceName, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = device.DeviceName, TextTrimming = TextTrimming.CharacterEllipsis });
            var connected = _host.SessionFor(device.DeviceId) is { IsRunning: true };
            text.Children.Add(new TextBlock { Text = connected ? "Connected" : "Not connected", FontSize = 12, Opacity = 0.65 });
            row.Children.Add(text);
            var forget = new Button { Content = "Forget" };
            var id = device.DeviceId;
            var name = device.DeviceName;
            forget.Click += async (_, _) =>
            {
                forget.IsEnabled = false;
                try
                {
                    await _host.RevokeAsync(id);
                    SettingsMessageText.Text = "";
                    StatusText.Text = $"Forgot {name}";
                }
                catch (Exception) { SettingsMessageText.Text = $"Couldn't forget {name}. Please try again."; }
                await RenderPairedPhonesAsync();
                RefreshPhones();
            };
            Grid.SetColumn(forget, 1);
            row.Children.Add(forget);
            PairedPhonesPanel.Children.Add(row);
        }
    }

    private void OnTransferProgress(TransferProgress progress)
    {
        _history.Update(progress);
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
                        : $"Saved {Path.GetFileName(progress.SavedPath ?? progress.FileName)}";
                    break;
                case TransferStatus.Interrupted:
                    TransferProgressBar.Visibility = Visibility.Collapsed;
                    TransferText.Text = $"{name} paused at {progress.Fraction:P0} — it will continue when the phone reconnects.";
                    break;
                case TransferStatus.Failed:
                    TransferProgressBar.Visibility = Visibility.Collapsed;
                    TransferText.Text = sending ? $"{name} didn't transfer correctly. Use Retry below to send it again." : $"{name} didn't arrive correctly. Send it again from your phone.";
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

    private void OnClipboardToggled(object sender, RoutedEventArgs e)
    {
        _clipboard.Enabled = ClipboardToggle.IsOn;
        if (!_applyingSettings) _settings.Update(s => s with { ClipboardSync = ClipboardToggle.IsOn });
    }

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

    /// <summary>Per phone: two phones can have a notification with the same key. Tags are limited to 64 chars.</summary>
    private static string NotificationTag(string deviceId, string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId + "\n" + key)))[..32];

    private static string AppNameKey(string? deviceId, string key) => $"{deviceId}\n{key}";

    private void ShowPhoneNotification(ConnectedDevice phone, NotificationPostPayload note)
    {
        if (!_settings.Current.ShowPhoneNotifications) return;
        try
        {
            var dev = phone.DeviceId;
            // With several phones, say which one it's from.
            var attribution = _host.Sessions.Count > 1 ? $"{note.AppName} · {phone.DeviceName}" : note.AppName;
            var builder = new AppNotificationBuilder()
                .AddArgument("bridge", NotificationToastCommand.Open)
                .AddText(note.Title)
                .SetAttributionText(attribution);
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
                        new NotificationToastCommand(NotificationToastCommand.Reply, note.Key, action.Id, DeviceId: dev)).SetInputId(NotificationToastCommand.ReplyInputId));
                }
                else
                {
                    builder.AddButton(Button(action.Title, new NotificationToastCommand(NotificationToastCommand.Press, note.Key, action.Id, DeviceId: dev)));
                }
                buttons++;
            }
            if (note.CanDismiss == true)
                builder.AddButton(Button("Dismiss", new NotificationToastCommand(NotificationToastCommand.Dismiss, note.Key, DeviceId: dev)));
            if (note.AppId is not null)
                builder.AddButton(Button($"Stop {note.AppName} notifications",
                    new NotificationToastCommand(NotificationToastCommand.Mute, AppId: note.AppId, DeviceId: dev)).SetContextMenuPlacement());

            var notification = builder.BuildNotification();
            notification.Tag = NotificationTag(dev, note.Key);
            notification.Group = NotificationGroup;
            // The app's own update after a reply from here ("You: …"): update it quietly, don't pop up again.
            notification.SuppressDisplay = _notificationActions.TakeRecentAction(note.Key, dev);
            AppNotificationManager.Default.Show(notification);
            lock (AppNames) { AppNames[AppNameKey(dev, note.Key)] = note.AppName; if (AppNames.Count > 500) AppNames.Clear(); }
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
        if (command.Kind == NotificationToastCommand.CancelSend)
        {
            var dropped = _explorerSend.CancelPending();
            ShowSendToast(dropped > 0 ? $"Cancelled: {Items(dropped)} won't be sent." : "There was nothing waiting to send.");
            return;
        }
        if (command.Kind == NotificationToastCommand.OpenLink)
        {
            OpenLink(command.Key!); // Parse already re-checked it
            return;
        }
        if (command.Kind == NotificationToastCommand.CopyLink)
        {
            // The link came from the phone: don't let clipboard sync send it back.
            try { await _clipboard.WriteFromPeerAsync(command.Key!); }
            catch (Exception) { ShowInfo("Couldn't copy the link (another app is using the clipboard)."); }
            return;
        }
        if (command.Kind == NotificationToastCommand.Open)
        {
            DispatcherQueue.TryEnqueue(App.ShowMainWindow);
            return;
        }
        string? status;
        try { status = await command.RunAsync(_notificationActions, userInput, _cts.Token); }
        catch (OperationCanceledException) { return; }
        string appName = "the app";
        if (command.Key is not null)
            lock (AppNames)
            {
                var dev = command.DeviceId ?? _host.CurrentSession?.Device.DeviceId;
                if (AppNames.TryGetValue(AppNameKey(dev, command.Key), out var n)) appName = n;
            }
        if (command.FailureMessage(status, appName) is { } message) ShowInfo(message);
    }

    // ------------------------------------------------------------------ handoff (links and text)

    /// <summary>Opens a checked http(s) link in the default browser; never anything else.</summary>
    private void OpenLink(string url)
    {
        if (HandoffRules.CheckUrl(url) is not { } link) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.Url) { UseShellExecute = true }); }
        catch (Exception ex) { _diagnostics.Warn("handoff.open_failed", LogField.Error(ex)); ShowInfo("Windows couldn't open that link."); }
    }

    private static void ShowLinkToast(HandoffLink link, string phone)
    {
        try
        {
            var open = new AppNotificationButton("Open");
            foreach (var (k, v) in new NotificationToastCommand(NotificationToastCommand.OpenLink, link.Url).ToArguments()) open.AddArgument(k, v);
            var copy = new AppNotificationButton("Copy link");
            foreach (var (k, v) in new NotificationToastCommand(NotificationToastCommand.CopyLink, link.Url).ToArguments()) copy.AddArgument(k, v);
            var builder = new AppNotificationBuilder()
                .AddText($"Link from {phone}")
                .AddText(link.Title ?? link.Host)
                .AddButton(open)
                .AddButton(copy);
            if (link.Title is not null) builder.AddText(link.Host); // the real destination, always visible
            // A click on the toast body opens the link too.
            foreach (var (k, v) in new NotificationToastCommand(NotificationToastCommand.OpenLink, link.Url).ToArguments()) builder.AddArgument(k, v);
            var toast = builder.BuildNotification();
            toast.Group = HandoffToastGroup;
            AppNotificationManager.Default.Show(toast);
        }
        catch (Exception) { }
    }

    private static void ShowHandoffToast(string title, string message)
    {
        try
        {
            var toast = new AppNotificationBuilder().AddArgument("bridge", NotificationToastCommand.Open).AddText(title).AddText(message).BuildNotification();
            toast.Group = HandoffToastGroup;
            AppNotificationManager.Default.Show(toast);
        }
        catch (Exception) { }
    }

    /// <summary>Tray › "Send clipboard to phone": a lone link opens on the phone, other text goes on its clipboard.</summary>
    public async Task SendClipboardToPhoneAsync()
    {
        string? text = null;
        try { text = await _clipboardAccess.ReadTextAsync(); } catch (Exception) { }
        if (string.IsNullOrWhiteSpace(text)) { ShowHandoffToast("Nothing to send", "The clipboard has no text or link."); return; }
        string status;
        try { status = await _handoff.SendClipboardAsync(text, _cts.Token); }
        catch (OperationCanceledException) { return; }
        ShowHandoffResult(status);
    }

    /// <summary>A link (url) or text shared to Bridge from another app (Share menu).</summary>
    public async Task HandoffAsync(string? url, string? title, string? text)
    {
        string status;
        try { status = url is not null ? await _handoff.SendUrlAsync(url, title, _cts.Token) : await _handoff.SendTextAsync(text ?? "", _cts.Token); }
        catch (OperationCanceledException) { return; }
        ShowHandoffResult(status);
    }

    private static void ShowHandoffResult(string status)
    {
        if (HandoffService.Explain(status) is { } problem) ShowHandoffToast("Not sent", problem);
        else ShowHandoffToast("Sent to your phone", status == HandoffStatus.Copied
            ? "It's on your phone's clipboard."
            : "Tap the notification on your phone to open the link.");
    }

    // ------------------------------------------------------------------ Explorer (Send to / Share)

    private static string Items(int n) => n == 1 ? "1 item" : $"{n} items";

    /// <summary>Files and folders handed over by File Explorer (Send to, Share) or a second launch.</summary>
    public void HandleSendRequest(SendRequest request)
    {
        _ = Task.Run(async () =>
        {
            if (request.Count == 0)
            {
                ShowSendToast(request.Skipped.Count > 0 ? "Bridge couldn't find those files." : "Nothing to send.");
                return;
            }
            if (request.Skipped.Count > 0) ShowSendToast($"{Items(request.Skipped.Count)} couldn't be found and won't be sent.");
            try { await _explorerSend.SubmitAsync(request, _cts.Token); }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>One notification for the current Explorer send, updated in place.</summary>
    private static void ShowSendToast(string message, bool cancel = false)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("bridge", NotificationToastCommand.Open)
                .AddText("Bridge")
                .AddText(message);
            if (cancel)
            {
                var button = new AppNotificationButton("Cancel");
                foreach (var (k, v) in new NotificationToastCommand(NotificationToastCommand.CancelSend).ToArguments()) button.AddArgument(k, v);
                builder.AddButton(button);
            }
            var toast = builder.BuildNotification();
            toast.Tag = SendToastTag;
            toast.Group = "bridge-explorer";
            AppNotificationManager.Default.Show(toast);
        }
        catch (Exception) { }
    }

    private static void ShowInfo(string message)
    {
        try { AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText("Bridge").AddText(message).BuildNotification()); }
        catch (Exception) { }
    }

    private static async void RemovePhoneNotification(string deviceId, string key)
    {
        try { await AppNotificationManager.Default.RemoveByTagAndGroupAsync(NotificationTag(deviceId, key), NotificationGroup); }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ first run

    private void ShowOnboardingIfNeeded()
    {
        var first = !_settings.Current.OnboardingDone;
        OnboardingPanel.Visibility = first ? Visibility.Visible : Visibility.Collapsed;
        MainPanel.Visibility = first ? Visibility.Collapsed : Visibility.Visible;
        SettingsButton.Visibility = first ? Visibility.Collapsed : Visibility.Visible;
        PhotosButton.Visibility = first ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnOnboardingDoneClicked(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s with { OnboardingDone = true });
        ShowOnboardingIfNeeded();
        if (_settings.Current.SendToMenu) _ = Task.Run(() => ExplorerIntegration.SetSendTo(true));
    }

    // ------------------------------------------------------------------ settings

    private void ApplySettingsToUi()
    {
        _applyingSettings = true;
        try
        {
            var s = _settings.Current;
            _host.DownloadDirectory = _downloadDir;
            DownloadFolderText.Text = _downloadDir;
            ResetFolderButton.IsEnabled = s.DownloadFolder is not null;
            StartWithWindowsToggle.IsOn = s.StartWithWindows;
            StartMinimizedToggle.IsOn = s.StartMinimized;
            ShowNotificationsToggle.IsOn = s.ShowPhoneNotifications;
            DiagnosticsToggle.IsOn = s.Diagnostics;
            SaveReportButton.IsEnabled = s.Diagnostics;
            ClipboardToggle.IsOn = s.ClipboardSync;
            QuickAccessToggle.IsOn = s.QuickAccessPin;
            SendToToggle.IsOn = s.SendToMenu;
            ReceiveHandoffToggle.IsOn = s.ReceiveHandoff;
            OpenLinksToggle.IsOn = s.OpenLinksAutomatically;
            OpenLinksToggle.IsEnabled = s.ReceiveHandoff;
            SendToToggle.Visibility = StartupRegistration.IsPackaged ? Visibility.Collapsed : Visibility.Visible;
            ShareHintText.Text = StartupRegistration.IsPackaged
                ? "In File Explorer, right-click files or folders and choose Share › Bridge to send them to your phone."
                : "In File Explorer, right-click files or folders and choose Send to › Bridge (your phone).";
            _diagnostics.Enabled = s.Diagnostics;
            VersionText.Text = $"Bridge {AppVersion}{(StartupRegistration.IsPackaged ? "" : " (unpackaged)")}";
        }
        finally { _applyingSettings = false; }
    }

    private async void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        SettingsMessageText.Text = "";
        // The user (or policy) may have turned start-at-sign-in off in Windows' own settings.
        var actual = await StartupRegistration.IsEnabledAsync();
        if (actual is { } on && on != _settings.Current.StartWithWindows) _settings.Update(s => s with { StartWithWindows = on });
        ApplySettingsToUi();
        _ = RenderPairedPhonesAsync();
        SettingsPanel.Visibility = Visibility.Visible;
        MainPanel.Visibility = Visibility.Collapsed;
        PhotosPanel.Visibility = Visibility.Collapsed;
        SettingsButton.Visibility = Visibility.Collapsed;
        PhotosButton.Visibility = Visibility.Collapsed;
    }

    private void OnSettingsDoneClicked(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;
        SettingsButton.Visibility = Visibility.Visible;
        PhotosButton.Visibility = Visibility.Visible;
    }

    private async void OnSettingToggled(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings) return;
        SettingsMessageText.Text = "";
        if (ReferenceEquals(sender, StartWithWindowsToggle))
        {
            var want = StartWithWindowsToggle.IsOn;
            var ok = await StartupRegistration.SetAsync(want);
            if (!ok && want) SettingsMessageText.Text = "Windows didn't allow Bridge to start at sign-in. You can turn it on in Settings › Apps › Startup.";
            _settings.Update(s => s with { StartWithWindows = want && ok });
        }
        else if (ReferenceEquals(sender, SendToToggle))
        {
            var want = SendToToggle.IsOn;
            if (!ExplorerIntegration.SetSendTo(want)) SettingsMessageText.Text = "Couldn't change Explorer's Send to menu.";
            _settings.Update(s => s with { SendToMenu = want });
        }
        else if (ReferenceEquals(sender, QuickAccessToggle))
        {
            var want = QuickAccessToggle.IsOn;
            if (!ExplorerIntegration.SetQuickAccessPin(_downloadDir, want)) SettingsMessageText.Text = "Couldn't change Quick access.";
            _settings.Update(s => s with { QuickAccessPin = want });
        }
        else if (ReferenceEquals(sender, DiagnosticsToggle))
        {
            var on = DiagnosticsToggle.IsOn;
            if (!on) { _diagnostics.Enabled = false; _diagnostics.Clear(); } // off means nothing kept
            _settings.Update(s => s with { Diagnostics = on });
            if (on) { _diagnostics.Enabled = true; _diagnostics.Info("diagnostics.enabled"); }
        }
        else
        {
            _settings.Update(s => s with
            {
                StartMinimized = StartMinimizedToggle.IsOn,
                ShowPhoneNotifications = ShowNotificationsToggle.IsOn,
                ReceiveHandoff = ReceiveHandoffToggle.IsOn,
                OpenLinksAutomatically = OpenLinksToggle.IsOn,
            });
        }
        ApplySettingsToUi();
    }

    private async void OnChangeFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new global::Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            if (SettingsStore.ValidFolder(folder.Path) is null)
            {
                SettingsMessageText.Text = "Choose a folder on this PC (network folders aren't supported).";
                return;
            }
            Directory.CreateDirectory(folder.Path);
            var old = _downloadDir;
            _settings.Update(s => s with { DownloadFolder = folder.Path });
            MoveQuickAccessPin(old);
            ApplySettingsToUi();
        }
        catch (Exception ex)
        {
            SettingsMessageText.Text = "Couldn't use that folder. Please choose another.";
            _diagnostics.Warn("settings.folder_failed", LogField.Error(ex));
        }
    }

    /// <summary>The received-files folder moved: move its Quick access pin with it.</summary>
    private void MoveQuickAccessPin(string oldFolder)
    {
        if (!_settings.Current.QuickAccessPin || string.Equals(oldFolder, _downloadDir, StringComparison.OrdinalIgnoreCase)) return;
        ExplorerIntegration.SetQuickAccessPin(oldFolder, false);
        ExplorerIntegration.SetQuickAccessPin(_downloadDir, true);
    }

    private void OnResetFolderClicked(object sender, RoutedEventArgs e)
    {
        var old = _downloadDir;
        _settings.Update(s => s with { DownloadFolder = null });
        MoveQuickAccessPin(old);
        ApplySettingsToUi();
    }

    private void OnSaveReportClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = _settings.Current;
            var report = DiagnosticsReport.Build(_diagnostics, DateTimeOffset.UtcNow,
                LogField.Version("app_version", typeof(MainWindow).Assembly.GetName().Version),
                LogField.Version("os_version", Environment.OSVersion.Version),
                LogField.Code("arch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()),
                LogField.Flag("packaged", StartupRegistration.IsPackaged),
                LogField.Flag("connected", _session is not null),
                LogField.Num("phones_connected", _host.Sessions.Count),
                LogField.Flag("custom_download_folder", s.DownloadFolder is not null),
                LogField.Flag("start_with_windows", s.StartWithWindows),
                LogField.Flag("show_notifications", s.ShowPhoneNotifications),
                LogField.Flag("clipboard_sync", s.ClipboardSync),
                LogField.Flag("receive_handoff", s.ReceiveHandoff),
                LogField.Flag("open_links", s.OpenLinksAutomatically),
                LogField.Flag("discovery", _discovery is not null));
            Directory.CreateDirectory(_downloadDir);
            var path = Path.Combine(_downloadDir, $"bridge-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, report);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            SettingsMessageText.Text = "";
        }
        catch (Exception ex)
        {
            SettingsMessageText.Text = "Couldn't save the report. Check that the received-files folder is available.";
            _diagnostics.Warn("diagnostics.report_failed", LogField.Error(ex));
        }
    }

    private void OnOpenLogFolderClicked(object sender, RoutedEventArgs e)
    {
        var dir = _diagnostics.Directory ?? _dataDir;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ photos

    private void OnPhotosClicked(object sender, RoutedEventArgs e)
    {
        MainPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        PhotosPanel.Visibility = Visibility.Visible;
        PhotosButton.Visibility = Visibility.Collapsed;
        if (_photosStale || _photoTiles.Count == 0) _ = ReloadPhotosAsync();
    }

    private void OnPhotosBackClicked(object sender, RoutedEventArgs e)
    {
        PhotosPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;
        PhotosButton.Visibility = Visibility.Visible;
    }

    private void OnPhotosRefreshClicked(object sender, RoutedEventArgs e) => _ = ReloadPhotosAsync();

    private void OnPhotosMoreClicked(object sender, RoutedEventArgs e) => _ = LoadMorePhotosAsync(_photoGeneration);

    private async Task ReloadPhotosAsync()
    {
        _photosStale = false;
        var generation = ++_photoGeneration;
        _photoTiles.Clear();
        _photoCursor = null;
        await LoadMorePhotosAsync(generation);
    }

    /// <summary>The grid shows at most this many (the newest); older ones are on the phone.</summary>
    private const int MaxPhotoTiles = 1200;

    private async Task LoadMorePhotosAsync(int generation)
    {
        PhotosMoreButton.IsEnabled = false;
        PhotosRefreshButton.IsEnabled = false;
        if (_session is null) PhotosStatusText.Text = PhotoBrowser.Explain(PhotoStatus.Unavailable);
        else if (!_photos.PhoneSupportsPhotos) PhotosStatusText.Text = "Update Bridge on your phone to see its photos here.";
        else PhotosStatusText.Text = "Loading…";
        try
        {
            if (_session is null || !_photos.PhoneSupportsPhotos) { PhotosMoreButton.Visibility = Visibility.Collapsed; return; }
            var page = await _photos.ListAsync(_photoCursor);
            if (generation != _photoGeneration) return; // a newer reload took over
            if (page.Status != PhotoStatus.Ok)
            {
                PhotosStatusText.Text = PhotoBrowser.Explain(page.Status);
                PhotosMoreButton.Visibility = Visibility.Collapsed;
                return;
            }
            var tiles = page.Items.Select(i => new PhotoTile(i)).ToList();
            foreach (var t in tiles) _photoTiles.Add(t);
            _photoCursor = page.Next;
            var full = _photoTiles.Count >= MaxPhotoTiles;
            PhotosMoreButton.Visibility = page.Next is null || full ? Visibility.Collapsed : Visibility.Visible;
            PhotosStatusText.Text = _photoTiles.Count == 0 ? "No photos on your phone (or none Bridge is allowed to see)."
                : full ? $"Showing the newest {_photoTiles.Count} photos." : $"{_photoTiles.Count} photos";
            _ = LoadThumbnailsAsync(tiles, generation);
        }
        finally
        {
            PhotosMoreButton.IsEnabled = true;
            PhotosRefreshButton.IsEnabled = true;
        }
    }

    /// <summary>Fills in previews a few at a time (the phone makes them one by one).</summary>
    private async Task LoadThumbnailsAsync(IReadOnlyList<PhotoTile> tiles, int generation)
    {
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(tiles.Select(async tile =>
        {
            await gate.WaitAsync();
            try
            {
                if (generation != _photoGeneration) return;
                var thumb = await _photos.ThumbnailAsync(tile.Info.Id);
                if (thumb.Jpeg is null) return;
                // Decode at tile size, not the preview's 320 px: thousands of tiles must stay light.
                var bitmap = new BitmapImage { DecodePixelWidth = 200 };
                using var stream = new MemoryStream(thumb.Jpeg);
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                tile.Thumb = bitmap;
            }
            catch (Exception) { /* a preview that can't be shown stays blank */ }
            finally { gate.Release(); }
        }));
    }

    private void OnPhotosSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var n = PhotosGrid.SelectedItems.Count;
        PhotosSaveButton.IsEnabled = n > 0;
        PhotosOpenButton.IsEnabled = n == 1;
    }

    private async void OnPhotosSaveClicked(object sender, RoutedEventArgs e)
    {
        var chosen = PhotosGrid.SelectedItems.OfType<PhotoTile>().ToList();
        if (chosen.Count == 0) return;
        PhotosSaveButton.IsEnabled = false;
        string? last = null;
        string? problem = null;
        var saved = 0;
        foreach (var tile in chosen)
        {
            PhotosStatusText.Text = $"Saving {saved + 1} of {chosen.Count}…";
            var result = await _photos.FetchAsync(tile.Info.Id);
            if (result.Status == PhotoStatus.Ok) { saved++; last = result.SavedPath; }
            else problem = PhotoBrowser.Explain(result.Status);
            if (result.Status is PhotoStatus.Unavailable or PhotoStatus.Disabled or PhotoStatus.NoPermission) break;
        }
        PhotosStatusText.Text = saved == chosen.Count
            ? $"Saved {saved} photo{(saved == 1 ? "" : "s")} to {_downloadDir}"
            : $"Saved {saved} of {chosen.Count}. {problem}";
        PhotosSaveButton.IsEnabled = PhotosGrid.SelectedItems.Count > 0;
        if (last is not null)
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{last}\"") { UseShellExecute = true }); } catch (Exception) { }
    }

    private async void OnPhotosOpenClicked(object sender, RoutedEventArgs e) => await OpenPhotoAsync(PhotosGrid.SelectedItem as PhotoTile);

    private async void OnPhotosDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) =>
        await OpenPhotoAsync((e.OriginalSource as FrameworkElement)?.DataContext as PhotoTile ?? PhotosGrid.SelectedItem as PhotoTile);

    private async Task OpenPhotoAsync(PhotoTile? tile)
    {
        if (tile is null) return;
        PhotosStatusText.Text = "Getting the photo…";
        var result = await _photos.FetchAsync(tile.Info.Id);
        if (result.Status != PhotoStatus.Ok || result.SavedPath is null) { PhotosStatusText.Text = PhotoBrowser.Explain(result.Status); return; }
        PhotosStatusText.Text = $"Saved to {result.SavedPath}";
        try { Process.Start(new ProcessStartInfo(result.SavedPath) { UseShellExecute = true }); } catch (Exception) { }
    }

    // ------------------------------------------------------------------ recent transfers

    private int _renderQueued;

    /// <summary>Progress arrives per 64 KB chunk; redraw the list at most ~4 times a second.</summary>
    private void QueueRenderRecent()
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) == 1) return;
        _ = Task.Delay(250).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
        {
            Volatile.Write(ref _renderQueued, 0);
            RenderRecent();
        }), TaskScheduler.Default);
    }

    private void RenderRecent()
    {
        var entries = _history.Entries;
        RecentPanel.Children.Clear();
        ClearRecentButton.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (entries.Count == 0) { RecentPanel.Children.Add(RecentEmptyText); return; }
        foreach (var entry in entries)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            var arrow = entry.Direction == TransferDirection.Sending ? "↑" : "↓";
            var title = entry.Folder is null ? entry.FileName : $"{entry.Folder} › {entry.FileName}";
            if (entry.Direction == TransferDirection.Sending && entry.Folder is not null && entry.Status == TransferStatus.Failed)
                title += " (send the folder again to retry)";
            text.Children.Add(new TextBlock { Text = $"{arrow} {title}", TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = TransferHistory.Describe(entry), FontSize = 12, Opacity = 0.65 });
            row.Children.Add(text);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (entry.CanRetry)
            {
                var retry = new Button { Content = "Retry", IsEnabled = _session is not null };
                ToolTipService.SetToolTip(retry, _session is null ? "Connect your phone first" : "Send it again");
                var path = entry.SourcePath!;
                var oldId = entry.TransferId;
                retry.Click += async (_, _) =>
                {
                    retry.IsEnabled = false;
                    if (_session is not { } s) return;
                    _history.Remove(oldId); // the new attempt gets its own row
                    _diagnostics.Info("transfer.retry");
                    await s.SendFileAsync(path);
                };
                buttons.Children.Add(retry);
            }
            if (entry.CanOpen)
            {
                var saved = entry.SavedPath!;
                var open = new Button { Content = "Open" };
                open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(saved) { UseShellExecute = true }); } catch (Exception) { } };
                var show = new Button { Content = "Show in folder" };
                show.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{saved}\"") { UseShellExecute = true });
                buttons.Children.Add(open);
                buttons.Children.Add(show);
            }
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            RecentPanel.Children.Add(row);
        }
    }

    private void OnClearRecentClicked(object sender, RoutedEventArgs e) => _history.ClearFinished();

    public async Task ShutdownAsync()
    {
        _qrTimer.Stop();
        _cts.Cancel();
        _discovery?.Dispose();
        _fileTransfer.State.Dispose(); // write any pending resume state before exiting
        _diagnostics.Info("app.exit");
        await _host.DisposeAsync();
        _listener.Dispose();
    }
}
