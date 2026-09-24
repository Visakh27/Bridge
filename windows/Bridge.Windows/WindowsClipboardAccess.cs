using Windows.ApplicationModel.DataTransfer;
using Bridge.Network;

namespace Bridge.Windows;

/// <summary>Real Windows Clipboard implementation of IClipboardAccess, using the WinRT DataTransfer APIs.</summary>
public sealed class WindowsClipboardAccess : IClipboardAccess
{
    public event Action<string>? LocalClipboardChanged;

    public WindowsClipboardAccess()
    {
        Clipboard.ContentChanged += async (_, _) =>
        {
            var text = await ReadTextAsync();
            if (text is not null) LocalClipboardChanged?.Invoke(text);
        };
    }

    public async Task<string?> ReadTextAsync()
    {
        var view = Clipboard.GetContent();
        if (!view.Contains(StandardDataFormats.Text)) return null;
        return await view.GetTextAsync();
    }

    public Task WriteTextAsync(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        return Task.CompletedTask;
    }
}
