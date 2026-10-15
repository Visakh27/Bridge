using Microsoft.Win32;

namespace Bridge.Windows;

/// <summary>
/// "Start Bridge when I sign in". Packaged (MSIX): the manifest's StartupTask
/// ("BridgeStartup"), which Windows lets the user veto in Settings › Apps ›
/// Startup. Unpackaged: the per-user Run key, launching with --minimized.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Bridge";
    public const string TaskId = "BridgeStartup";
    public const string MinimizedArg = "--minimized";

    public static bool IsPackaged
    {
        get
        {
            try { return global::Windows.ApplicationModel.Package.Current is not null; }
            catch (Exception) { return false; } // unpackaged: Package.Current throws
        }
    }

    /// <summary>Whether Windows will start Bridge at sign-in; null if it can't be read.</summary>
    public static async Task<bool?> IsEnabledAsync()
    {
        try
        {
            if (IsPackaged)
            {
                var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
                return task.State is global::Windows.ApplicationModel.StartupTaskState.Enabled
                    or global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            }
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Turns start-at-sign-in on or off. False if Windows (or policy, or the user) refused.</summary>
    public static async Task<bool> SetAsync(bool enabled)
    {
        try
        {
            if (IsPackaged)
            {
                var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
                if (!enabled) { task.Disable(); return true; }
                var state = await task.RequestEnableAsync();
                return state is global::Windows.ApplicationModel.StartupTaskState.Enabled
                    or global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            }
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {MinimizedArg}");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
