using System.Runtime.InteropServices;
using Bridge.Core;

namespace Bridge.Windows;

/// <summary>
/// File Explorer hooks (best effort; nothing here may stop Bridge from starting):
/// "Send to → Bridge (your phone)" for the unpackaged app (a shortcut in the
/// user's SendTo folder that runs <c>Bridge.Windows.exe --send …</c>; the MSIX
/// uses the Share menu, declared in the manifest), and pinning the
/// received-files folder to Quick access.
/// </summary>
internal static class ExplorerIntegration
{
    private const string ShortcutName = "Bridge (your phone).lnk";

    private static string SendToShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), ShortcutName);

    /// <summary>Adds, refreshes (the app may have moved) or removes the Send to entry. False on failure.</summary>
    public static bool SetSendTo(bool enabled)
    {
        if (StartupRegistration.IsPackaged) return true; // the Share menu covers it
        try
        {
            if (!enabled)
            {
                if (File.Exists(SendToShortcut)) File.Delete(SendToShortcut);
                return true;
            }
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return false;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic link = shell.CreateShortcut(SendToShortcut);
                link.TargetPath = Environment.ProcessPath;
                link.Arguments = LaunchArguments.SendFlag; // Explorer appends the selected paths
                link.WorkingDirectory = AppContext.BaseDirectory;
                link.Description = "Send to your phone with Bridge";
                var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Bridge.ico");
                if (File.Exists(icon)) link.IconLocation = icon;
                link.Save();
                Marshal.FinalReleaseComObject(link);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool SendToInstalled => !StartupRegistration.IsPackaged && File.Exists(SendToShortcut);

    /// <summary>Pins (or unpins) <paramref name="folder"/> in Quick access. False on failure.</summary>
    public static bool SetQuickAccessPin(string folder, bool pinned)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return false;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                if (pinned)
                {
                    Directory.CreateDirectory(folder);
                    shell.Namespace(folder).Self.InvokeVerb("pintohome");
                    return true;
                }
                // Quick access lists pinned folders; unpin ours if it's there.
                dynamic home = shell.Namespace("shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}");
                dynamic items = home.Items();
                int count = items.Count; // indexed: enumerating a COM collection through dynamic isn't reliable
                for (var i = 0; i < count; i++)
                {
                    dynamic item = items.Item(i);
                    if (string.Equals((string)item.Path, folder, StringComparison.OrdinalIgnoreCase))
                    {
                        item.InvokeVerb("unpinfromhome");
                        break;
                    }
                }
                return true;
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }
}
