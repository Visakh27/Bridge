using Bridge.Protocol;

namespace Bridge.Network;

/// <summary>
/// Platform-agnostic clipboard sync coordinator. The actual clipboard I/O is
/// injected via IClipboardAccess so this class has no Windows-specific
/// dependency (the real Windows.ApplicationModel.DataTransfer / WinUI
/// clipboard calls live in Bridge.Windows and implement this interface).
/// </summary>
public interface IClipboardAccess
{
    Task<string?> ReadTextAsync();
    Task WriteTextAsync(string text);
    event Action<string>? LocalClipboardChanged; // fired when the *local* OS clipboard changes
}

public sealed class ClipboardSyncService
{
    private readonly IClipboardAccess _clipboard;
    private bool _enabled;
    private bool _suppressNextLocalEvent;

    public bool TextOnly { get; set; } = true;

    public ClipboardSyncService(IClipboardAccess clipboard)
    {
        _clipboard = clipboard;
        _clipboard.LocalClipboardChanged += OnLocalClipboardChanged;
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public EncryptedChannel? Channel { get; set; }

    private async void OnLocalClipboardChanged(string newText)
    {
        if (!_enabled || Channel is null) return;
        if (_suppressNextLocalEvent) { _suppressNextLocalEvent = false; return; }
        if (ContainsLikelySensitiveContent(newText)) return;

        await Channel.SendAsync(BridgeMessage.Create(MessageType.ClipboardUpdate,
            new ClipboardUpdatePayload("text", newText)));
    }

    /// <summary>Call for every incoming BridgeMessage of type clipboard.update.</summary>
    public async Task HandleIncomingAsync(BridgeMessage message)
    {
        if (!_enabled) return;
        var payload = message.PayloadAs<ClipboardUpdatePayload>();
        if (payload.Format != "text") return;

        _suppressNextLocalEvent = true;
        await _clipboard.WriteTextAsync(payload.Data);
    }

    /// <summary>Basic heuristic exclusions (section 10): avoid syncing things that look like secrets.</summary>
    private static bool ContainsLikelySensitiveContent(string text)
    {
        if (text.Length > 10_000) return true; // avoid huge blobs
        var lowered = text.ToLowerInvariant();
        return lowered.Contains("-----begin") // private keys / certs
            || lowered.Contains("password:")
            || lowered.Contains("secret_key");
    }
}
