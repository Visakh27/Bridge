using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Bridge.Windows;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN) so only one Bridge runs:
/// a second launch — the Start menu while Bridge sits in the tray, a toast
/// click Windows routed to a new process — hands its activation to the
/// running instance (which shows its window or handles the toast) and exits.
/// Two instances would fight over the listening port and show two tray icons.
/// Pattern from the Windows App SDK single-instancing docs.
/// </summary>
public static class Program
{
    private const string InstanceKey = "Bridge.Windows.Main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectedToRunningInstance()) return 0;
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    private static bool RedirectedToRunningInstance()
    {
        AppActivationArguments args;
        AppInstance key;
        try
        {
            args = AppInstance.GetCurrent().GetActivatedEventArgs();
            key = AppInstance.FindOrRegisterForKey(InstanceKey);
        }
        catch (Exception)
        {
            return false; // can't tell: run normally
        }
        if (key.IsCurrent)
        {
            key.Activated += (_, e) => App.OnRedirectedActivation(e);
            return false;
        }
        // Redirect off this STA thread, but keep pumping COM while waiting (as the docs advise).
        var done = CreateEvent(IntPtr.Zero, true, false, null);
        _ = Task.Run(() =>
        {
            try { key.RedirectActivationToAsync(args).AsTask().Wait(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            SetEvent(done);
        });
        _ = CoWaitForMultipleObjects(CWMO_DEFAULT, INFINITE, 1, [done], out _);
        CloseHandle(done);
        return true;
    }

    private const uint CWMO_DEFAULT = 0;
    private const uint INFINITE = 0xFFFFFFFF;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, ulong count, IntPtr[] handles, out uint index);
}
