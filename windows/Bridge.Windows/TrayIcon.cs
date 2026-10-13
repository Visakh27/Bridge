using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Bridge.Windows;

/// <summary>
/// System-tray presence (section 4.2 / 26): keeps Bridge reachable after the
/// window is closed. Built in code rather than XAML and driven by commands,
/// because H.NotifyIcon's default native popup menu only invokes Command, not
/// Click handlers. Generated icon until real branding assets exist.
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
            IconSource = new GeneratedIconSource
            {
                Text = "B",
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 0, 120, 212)),
            },
            ContextFlyout = menu,
            LeftClickCommand = openCommand,
            NoLeftClickDelay = true,
        };
        _icon.ForceCreate();
    }

    public void Dispose() => _icon.Dispose();

    private sealed class RelayCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
