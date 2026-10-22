using Bridge.Core;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace Bridge.Windows;

public partial class App : Application
{
    private TrayIcon? _tray;

    public static MainWindow? MainWindowInstance { get; private set; }

    /// <summary>Set by the tray's Exit item so closing the window really exits instead of hiding to the tray.</summary>
    public static bool IsExiting { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // Subscribing before Register() keeps toast clicks in this process.
            AppNotificationManager.Default.NotificationInvoked += (_, e) =>
                MainWindowInstance?.OnToastInvoked(e.Arguments, e.UserInput);
            // Needed to show mirrored phone notifications (works packaged and unpackaged).
            AppNotificationManager.Default.Register();
        }
        catch (Exception)
        {
            // Notifications unavailable on this system; everything else still works.
        }

        AppActivationArguments? activation = null;
        try { activation = AppInstance.GetCurrent().GetActivatedEventArgs(); } catch (Exception) { }

        MainWindowInstance = new MainWindow();
        // Started at sign-in (Run key with --minimized, or the MSIX StartupTask), or the user prefers
        // the tray: stay hidden. Never before the first-run guide has been seen.
        var atSignIn = Environment.GetCommandLineArgs().Contains(StartupRegistration.MinimizedArg)
            || activation?.Kind == ExtendedActivationKind.StartupTask;
        var fromToast = activation?.Kind == ExtendedActivationKind.AppNotification;
        // Explorer "Send to" (unpackaged) or "Share" (MSIX) started Bridge just to send: stay in the tray.
        var send = LaunchArguments.ParseSend(Environment.GetCommandLineArgs());
        var fromShare = activation?.Kind == ExtendedActivationKind.ShareTarget;
        // From a toast, OnToastInvoked decides: a reply or button press stays in the tray, a plain click opens the window.
        var quiet = fromToast || fromShare || (send is not null && MainWindowInstance.ShouldStartHidden(launchedAtSignIn: true));
        if (!quiet && !MainWindowInstance.ShouldStartHidden(atSignIn)) MainWindowInstance.Activate();
        if (send is not null) MainWindowInstance.HandleSendRequest(send);
        if (fromShare) _ = HandleShareAsync(activation!);

        // Launched by clicking a toast (e.g. a reply typed after Bridge was closed): carry it out
        // once the phone reconnects (NotificationActionRouter waits briefly for it).
        if (fromToast && activation!.Data is AppNotificationActivatedEventArgs toast)
            MainWindowInstance.OnToastInvoked(toast.Arguments, toast.UserInput);

        _tray = new TrayIcon(ShowMainWindow, () => _ = ExitAsync());
    }

    /// <summary>Another launch was handed to this instance (see Program): show the window, send files, or handle the toast.</summary>
    public static void OnRedirectedActivation(AppActivationArguments e)
    {
        var window = MainWindowInstance;
        if (window is null) return;
        if (e.Kind == ExtendedActivationKind.AppNotification && e.Data is AppNotificationActivatedEventArgs toast)
            window.OnToastInvoked(toast.Arguments, toast.UserInput);
        else if (e.Kind == ExtendedActivationKind.ShareTarget)
            _ = HandleShareAsync(e);
        else if (e.Data is global::Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs sendLaunch &&
                 LaunchArguments.ParseSend(sendLaunch.Arguments) is { } send)
            window.HandleSendRequest(send); // "Send to" while Bridge was already running
        else if (e.Kind == ExtendedActivationKind.StartupTask ||
                 (e.Data is global::Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch &&
                  (launch.Arguments ?? "").Contains(StartupRegistration.MinimizedArg)))
            return; // a sign-in start while already running: stay in the tray
        else
            window.DispatcherQueue.TryEnqueue(ShowMainWindow);
    }

    /// <summary>Explorer's Share menu (MSIX): take the shared files and folders, then let Explorer go.</summary>
    private static async Task HandleShareAsync(AppActivationArguments e)
    {
        if (e.Data is not global::Windows.ApplicationModel.Activation.IShareTargetActivatedEventArgs share) return;
        var operation = share.ShareOperation;
        try
        {
            var paths = new List<string>();
            if (operation.Data.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                foreach (var item in await operation.Data.GetStorageItemsAsync())
                    if (!string.IsNullOrEmpty(item.Path)) paths.Add(item.Path);
            operation.ReportCompleted();
            var request = LaunchArguments.ParseSend([LaunchArguments.SendFlag, .. paths]);
            if (request is not null) MainWindowInstance?.HandleSendRequest(request);
        }
        catch (Exception)
        {
            try { operation.ReportError("Bridge couldn't read what was shared."); } catch { }
        }
    }

    public static void ShowMainWindow()
    {
        if (MainWindowInstance is null) return;
        MainWindowInstance.AppWindow.Show();
        MainWindowInstance.Activate();
    }

    private async Task ExitAsync()
    {
        IsExiting = true;
        if (MainWindowInstance is not null)
        {
            try { await MainWindowInstance.ShutdownAsync(); } catch { /* exiting anyway */ }
        }
        try { AppNotificationManager.Default.Unregister(); } catch { }
        _tray?.Dispose();
        Exit();
    }
}
