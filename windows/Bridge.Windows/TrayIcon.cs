using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Bridge.Windows;

/// <summary>
/// System-tray presence (section 4.2 / 26): keeps Bridge reachable after the
/// window is closed. Built in code rather than XAML and driven by commands,
/// because H.NotifyIcon's default native popup menu only invokes Command, not
/// Click handlers. Uses Assets/Bridge.ico; falls back to a generated "B" if it can't be loaded.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;

    public TrayIcon(Action open, Action exit)
    {
        var openCommand = new RelayCommand(open);
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "Open Bridge", Command = openCommand });
        menu.Items.Add(new MenuFlyoutItem { Text = "Exit", Command = new RelayCommand(exit) });

        _icon = new TaskbarIcon
        {
            ToolTipText = "Bridge",
            IconSource = LoadIcon(),
            ContextFlyout = menu,
            LeftClickCommand = openCommand,
            NoLeftClickDelay = true,
        };
        _icon.ForceCreate();
    }

    private static ImageSource LoadIcon()
    {
        try
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Bridge.ico")))
                return new BitmapImage(new Uri("ms-appx:///Assets/Bridge.ico"));
        }
        catch (Exception) { }
        return new GeneratedIconSource { Text = "B", Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 0, 120, 212)) };
    }

    public void Dispose() => _icon.Dispose();

    private sealed class RelayCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
