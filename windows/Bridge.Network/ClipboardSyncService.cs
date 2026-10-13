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
    public const int MaxTextLength = 10_000;

    private readonly IClipboardAccess _clipboard;
    private readonly object _gate = new();
    private string? _lastSynced; // last text sent or received, to break echo loops
    private volatile bool _enabled;
    private volatile EncryptedChannel? _channel;

    public ClipboardSyncService(IClipboardAccess clipboard)
    {
        _clipboard = clipboard;
        _clipboard.LocalClipboardChanged += OnLocalClipboardChanged;
    }

    /// <summary>Opt-in (docs/security.md); off by default.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Whether the peer advertised the "clipboard" feature.</summary>
    public bool PeerSupportsClipboard { get; set; }

    public EncryptedChannel? Channel
    {
        get => _channel;
        set => _channel = value;
    }

    /// <summary>Raised when an outgoing update couldn't be sent (never throws into the clipboard event).</summary>
    public event Action<Exception>? SendFailed;

    private async void OnLocalClipboardChanged(string newText)
    {
        // async void is an event handler: every exception must be caught here
        // or it would crash the process.
        try
        {
            var channel = _channel;
            if (!_enabled || !PeerSupportsClipboard || channel is null) return;
            if (ContainsLikelySensitiveContent(newText)) return;
            lock (_gate)
            {
                if (newText == _lastSynced) return; // our own write echoing back, or no change
                _lastSynced = newText;
            }

            await channel.SendAsync(BridgeMessage.Create(MessageType.ClipboardUpdate,
                new ClipboardUpdatePayload("text", newText)));
        }
        catch (Exception ex)
        {
            // Not delivered: forget it so copying the same text again retries.
            lock (_gate) { if (_lastSynced == newText) _lastSynced = null; }
            SendFailed?.Invoke(ex);
        }
    }

    /// <summary>Call for every incoming BridgeMessage of type clipboard.update.</summary>
    public async Task HandleIncomingAsync(BridgeMessage message)
    {
        if (!_enabled) return;
        var payload = message.PayloadAs<ClipboardUpdatePayload>();
        if (payload.Format != "text" || payload.Data is null || payload.Data.Length > MaxTextLength) return;

        lock (_gate)
        {
            if (payload.Data == _lastSynced) return;
            _lastSynced = payload.Data; // so the resulting local change event isn't echoed back
        }
        try
        {
            await _clipboard.WriteTextAsync(payload.Data);
        }
        catch
        {
            // Not applied: let the same text through if it's sent again.
            lock (_gate) { if (_lastSynced == payload.Data) _lastSynced = null; }
            throw;
        }
    }

    /// <summary>Basic heuristic exclusions (section 10): avoid syncing things that look like secrets.</summary>
    public static bool ContainsLikelySensitiveContent(string text)
    {
        if (text.Length > MaxTextLength) return true; // avoid huge blobs
        var lowered = text.ToLowerInvariant();
        return lowered.Contains("-----begin") // private keys / certs
            || lowered.Contains("password:")
            || lowered.Contains("secret_key");
    }
}
