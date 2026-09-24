using Microsoft.UI.Xaml;

namespace Bridge.Windows;

/// <summary>System-tray presence (section 4.2 / 26): shows connection state and offers quick access to the dashboard.</summary>
public sealed partial class TrayIcon
{
    public TrayIcon()
    {
        InitializeComponent();
    }

    private void OnOpenClicked(object sender, RoutedEventArgs e) => App.MainWindowInstance?.Activate();

    private void OnExitClicked(object sender, RoutedEventArgs e) => Application.Current.Exit();
}
