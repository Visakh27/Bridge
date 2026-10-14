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

        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();

        // Launched by clicking a toast (e.g. a reply typed after Bridge was closed): carry it out
        // once the phone reconnects (NotificationActionRouter waits briefly for it).
        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind == ExtendedActivationKind.AppNotification && activation.Data is AppNotificationActivatedEventArgs toast)
                MainWindowInstance.OnToastInvoked(toast.Arguments, toast.UserInput);
        }
        catch (Exception) { }

        _tray = new TrayIcon(ShowMainWindow, () => _ = ExitAsync());
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
