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
    private BridgeSession? _session;
    private readonly SettingsStore _settings;
    private readonly DiagnosticsLog _diagnostics;
    private readonly TransferHistory _history = new();
    private readonly PhotoBrowser _photos;
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
        _pairing = new PairingService(identity, store, Environment.MachineName);
        _clipboard = new ClipboardSyncService(new WindowsClipboardAccess(DispatcherQueue));
        _listener = new TcpBridgeListener(); // fixed port so paired phones can find us again
        _host = new BridgeHost(identity, store, _pairing, _fileTransfer, _clipboard, _downloadDir);
        _notificationActions = new NotificationActionRouter(_host);
        _photos = new PhotoBrowser(_host, _fileTransfer);
        _photos.Changed += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (PhotosPanel.Visibility == Visibility.Visible) _ = ReloadPhotosAsync(); else _photosStale = true;
        });
        PhotosGrid.ItemsSource = _photoTiles;
        _ = new BridgeDiagnostics(_diagnostics, _host, _fileTransfer, _notificationActions, _photos);
        _diagnostics.Info("app.started", LogField.Version("version", typeof(MainWindow).Assembly.GetName().Version),
            LogField.Version("os", Environment.OSVersion.Version), LogField.Flag("packaged", StartupRegistration.IsPackaged));

        _fileTransfer.ProgressChanged += OnTransferProgress;
        _history.Changed += QueueRenderRecent;
        ApplySettingsToUi();
        ShowOnboardingIfNeeded();
        // Unpackaged: re-point the sign-in entry at this copy of Bridge (it may have been moved or updated).
        if (_settings.Current.StartWithWindows && !StartupRegistration.IsPackaged) _ = StartupRegistration.SetAsync(true);
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
            if (_session?.Device.DeviceId != session.Device.DeviceId) { _photos.ClearCache(); _photosStale = true; }
            _session = session;
            _clipboard.Enabled = ClipboardToggle.IsOn;
            if (PhotosPanel.Visibility == Visibility.Visible && _photosStale) _ = ReloadPhotosAsync();
            PairingPanel.Visibility = Visibility.Collapsed;
            DevicePanel.Visibility = Visibility.Visible;
            DropZoneText.Text = "Drop files or folders here to send them to your phone";
            RenderRecent();
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
            DevicePanel.Visibility = Visibility.Collapsed;
            PairingPanel.Visibility = Visibility.Visible;
            DropZoneText.Text = "Connect your phone to send files";
            RenderRecent();
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

    private static string NotificationTag(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]; // tags are limited to 64 chars

    private void ShowPhoneNotification(NotificationPostPayload note)
    {
        if (!_settings.Current.ShowPhoneNotifications) return;
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
            _settings.Update(s => s with { DownloadFolder = folder.Path });
            ApplySettingsToUi();
        }
        catch (Exception ex)
        {
            SettingsMessageText.Text = "Couldn't use that folder. Please choose another.";
            _diagnostics.Warn("settings.folder_failed", LogField.Error(ex));
        }
    }

    private void OnResetFolderClicked(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s with { DownloadFolder = null });
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
                LogField.Flag("custom_download_folder", s.DownloadFolder is not null),
                LogField.Flag("start_with_windows", s.StartWithWindows),
                LogField.Flag("show_notifications", s.ShowPhoneNotifications),
                LogField.Flag("clipboard_sync", s.ClipboardSync),
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
