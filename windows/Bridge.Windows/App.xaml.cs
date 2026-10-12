using Microsoft.UI.Xaml;
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
            // Needed to show mirrored phone notifications (works packaged and unpackaged).
            AppNotificationManager.Default.Register();
        }
        catch (Exception)
        {
            // Notifications unavailable on this system; everything else still works.
        }

        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();

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
