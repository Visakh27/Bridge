using Bridge.Network;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace Bridge.Windows;

/// <summary>
/// Real Windows Clipboard implementation of IClipboardAccess, using the WinRT
/// DataTransfer APIs. Those APIs only work on the UI thread, while clipboard
/// updates from the phone arrive on the session's background thread, so every
/// call is marshalled through the window's DispatcherQueue.
/// </summary>
public sealed class WindowsClipboardAccess : IClipboardAccess
{
    private readonly DispatcherQueue _dispatcher;

    public event Action<string>? LocalClipboardChanged;

    /// <param name="dispatcher">The UI thread's queue; the constructor must run on that thread.</param>
    public WindowsClipboardAccess(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Clipboard.ContentChanged += OnContentChanged;
    }

    private async void OnContentChanged(object? sender, object e)
    {
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text)) return;
            var text = await view.GetTextAsync();
            if (text is not null) LocalClipboardChanged?.Invoke(text);
        }
        catch (Exception)
        {
            // Clipboard is locked by another process or held non-text data; skip this change.
        }
    }

    public Task<string?> ReadTextAsync() => OnUiThreadAsync(async () =>
    {
        var view = Clipboard.GetContent();
        return view.Contains(StandardDataFormats.Text) ? await view.GetTextAsync() : null;
    });

    /// <summary>
    /// Another app holding the clipboard open makes SetContent throw
    /// (CLIPBRD_E_CANT_OPEN), usually for a few milliseconds, so retry briefly.
    /// </summary>
    public Task WriteTextAsync(string text) => OnUiThreadAsync<object?>(async () =>
    {
        var package = new DataPackage();
        package.SetText(text);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetContent(package);
                Clipboard.Flush(); // keep the text available after Bridge exits
                return null;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5)
            {
                await Task.Delay(50 * attempt);
            }
        }
    });

    private Task<T> OnUiThreadAsync<T>(Func<Task<T>> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcher.TryEnqueue(async () =>
        {
            try { tcs.SetResult(await action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        if (!queued) tcs.SetException(new InvalidOperationException("UI thread is shutting down"));
        return tcs.Task;
    }
}
