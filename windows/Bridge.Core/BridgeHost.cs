using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;

namespace Bridge.Core;

/// <summary>
/// PC-side connection front door. Accepts TCP connections, runs the
/// authenticated handshake, then looks at the first message:
///   • pairing.request          → pairing (token + key proof), then the phone disconnects;
///   • capabilities.advertise   → a paired phone (re)connecting: verify its
///                                identity signature, reply with our
///                                capabilities and start a BridgeSession;
///   • anything else / unknown  → error + close.
/// Each connection is handled independently, so one stalled or malicious
/// client can't block pairing or reconnects.
///
/// Several paired phones can be connected at once (up to <see cref="MaxConnectedPhones"/>),
/// one session each; a phone reconnecting replaces only its own stale session.
/// One of them is the <em>active</em> phone (<see cref="CurrentSession"/>): the one
/// the user picked, else the most recently connected. Sends, photos and handoffs
/// without an explicit phone go to it.
/// </summary>
public sealed class BridgeHost : IAsyncDisposable
{
    public static readonly string[] LocalFeatures = [Features.Files, Features.Clipboard, Features.Notifications, Features.FilesResume, Features.FilesFolders, Features.NotificationsActions, Features.Photos, Features.Handoff];
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan FirstMessageTimeout = TimeSpan.FromSeconds(30);
    public const int MaxConnectedPhones = 8;

    private readonly DeviceIdentity _identity;
    private readonly IPairedDeviceStore _store;
    private readonly PairingService _pairing;
    private readonly FileTransferService _fileTransfer;
    private readonly ClipboardSyncService _clipboard;
    private string _downloadDir;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    /// <summary>Live sessions by phone, oldest first (the last one is the most recently connected).</summary>
    private readonly List<BridgeSession> _sessions = [];
    private string? _activeDeviceId;

    public BridgeHost(DeviceIdentity identity, IPairedDeviceStore store, PairingService pairing,
        FileTransferService fileTransfer, ClipboardSyncService clipboard, string downloadDir)
    {
        _identity = identity;
        _store = store;
        _pairing = pairing;
        _fileTransfer = fileTransfer;
        _clipboard = clipboard;
        _downloadDir = downloadDir;
    }

    /// <summary>The active phone's session: the picked phone if it's connected, else the most recently connected one.</summary>
    public BridgeSession? CurrentSession
    {
        get
        {
            lock (_gate)
            {
                if (_activeDeviceId is { } id && _sessions.FindLast(s => s.Device.DeviceId == id) is { } picked) return picked;
                return _sessions.Count > 0 ? _sessions[^1] : null;
            }
        }
    }

    /// <summary>Every connected phone's session, oldest connection first.</summary>
    public IReadOnlyList<BridgeSession> Sessions { get { lock (_gate) return [.. _sessions]; } }

    /// <summary>The session of one phone, or null if it isn't connected.</summary>
    public BridgeSession? SessionFor(string? deviceId)
    {
        if (deviceId is null) return null;
        lock (_gate) return _sessions.FindLast(s => s.Device.DeviceId == deviceId);
    }

    /// <summary>
    /// The phone the user picked (null = follow the most recently connected). It needn't be
    /// connected: while it's away, <see cref="CurrentSession"/> falls back to another phone.
    /// </summary>
    public string? ActiveDeviceId
    {
        get { lock (_gate) return _activeDeviceId; }
        set
        {
            lock (_gate) _activeDeviceId = value;
            RaiseActiveChanged();
        }
    }

    private readonly object _raiseGate = new();
    private BridgeSession? _lastRaisedActive;

    /// <summary>
    /// Tells subscribers the active phone as it is <em>now</em>, once per change, in order — even when
    /// a connect, a disconnect and the user's pick happen at the same moment on different threads.
    /// </summary>
    private void RaiseActiveChanged()
    {
        lock (_raiseGate)
        {
            var now = CurrentSession;
            if (ReferenceEquals(now, _lastRaisedActive)) return;
            _lastRaisedActive = now;
            ActiveSessionChanged?.Invoke(now);
        }
    }

    /// <summary>The active phone changed (picked, connected, or the active one left). Null: no phone connected.</summary>
    public event Action<BridgeSession?>? ActiveSessionChanged;

    /// <summary>Where received files go; a change applies to the next file, also in a live session.</summary>
    public string DownloadDirectory
    {
        get => Volatile.Read(ref _downloadDir);
        set => Volatile.Write(ref _downloadDir, value);
    }

    public event Action<PairedDevice>? DevicePaired;
    /// <summary>Raised on a new session; subscribe to the session's own events from here.</summary>
    public event Action<BridgeSession>? SessionStarted;
    public event Action<BridgeSession>? SessionEnded;
    /// <summary>Diagnostics only (no payload contents): why a connection was refused or failed.</summary>
    public event Action<string>? ConnectionRejected;

    /// <summary>Accept loop. Runs until <paramref name="ct"/> is cancelled or the host is disposed.</summary>
    public async Task RunAsync(TcpBridgeListener listener, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        while (!linked.IsCancellationRequested)
        {
            TcpTransport transport;
            try
            {
                transport = await listener.AcceptAsync(linked.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (System.Net.Sockets.SocketException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None);
                continue;
            }

            _ = HandleConnectionAsync(transport, linked.Token); // handled concurrently; never throws
        }
    }

    /// <summary>Handles one accepted connection. Never throws.</summary>
    public async Task HandleConnectionAsync(ITransport transport, CancellationToken ct)
    {
        EncryptedChannel? channel = null;
        var handedOff = false;
        try
        {
            using (var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                handshakeTimeout.CancelAfter(HandshakeTimeout);
                channel = await EncryptedChannel.HandshakeAsResponderAsync(transport, _identity, handshakeTimeout.Token);
            }

            BridgeMessage first;
            using (var firstTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                firstTimeout.CancelAfter(FirstMessageTimeout);
                first = await channel.ReceiveAsync(firstTimeout.Token);
            }

            switch (first.Type)
            {
                case MessageType.PairingRequest:
                {
                    var device = await _pairing.HandlePairingRequestAsync(channel, first, ct);
                    if (device is null) ConnectionRejected?.Invoke("Pairing request rejected");
                    else DevicePaired?.Invoke(device);
                    break; // pairing connections end here; the phone reconnects for the session
                }
                case MessageType.CapabilitiesAdvertise:
                    handedOff = await StartSessionAsync(channel, first, ct);
                    break;
                default:
                    await SendErrorAsync(channel, ErrorCodes.AuthFailed, "Expected pairing.request or capabilities.advertise", ct);
                    ConnectionRejected?.Invoke($"Unexpected first message {first.Type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            ConnectionRejected?.Invoke($"Connection failed during setup: {ex.GetType().Name}");
        }
        finally
        {
            if (!handedOff)
            {
                if (channel is not null) await channel.DisposeAsync();
                else await transport.DisposeAsync();
            }
        }
    }

    private async Task<bool> StartSessionAsync(EncryptedChannel channel, BridgeMessage capabilities, CancellationToken ct)
    {
        var paired = (await _store.ListAsync()).FirstOrDefault(d => d.DeviceId == channel.PeerDeviceId);
        if (paired is null)
        {
            await SendErrorAsync(channel, ErrorCodes.DeviceUnpaired, "This device is not paired with this PC", ct);
            ConnectionRejected?.Invoke("Unpaired device tried to connect");
            return false;
        }
        if (!channel.VerifyPeer(paired.LongTermPublicKey))
        {
            await SendErrorAsync(channel, ErrorCodes.AuthFailed, "Device identity could not be verified", ct);
            ConnectionRejected?.Invoke("Identity signature check failed");
            return false;
        }

        channel.MarkPeerAuthenticated(); // file chunks etc. may now use full-size frames
        var device = new ConnectedDevice
        {
            DeviceId = paired.DeviceId,
            DeviceName = paired.DeviceName,
            PeerFeatures = capabilities.PayloadAs<CapabilitiesPayload>().Features ?? [],
        };
        await _sessionLock.WaitAsync(ct);
        try
        {
            BridgeSession? previous;
            bool full;
            lock (_gate)
            {
                // A phone reconnecting replaces its own stale (half-open) session; a new phone needs a free place.
                previous = _sessions.FindLast(s => s.Device.DeviceId == device.DeviceId);
                full = previous is null && _sessions.Count >= MaxConnectedPhones;
            }
            if (full)
            {
                await SendErrorAsync(channel, ErrorCodes.TooManyDevices, $"This PC already has {MaxConnectedPhones} phones connected", ct);
                ConnectionRejected?.Invoke("Too many phones connected");
                return false;
            }
            await channel.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload(LocalFeatures)), ct);
            var session = new BridgeSession(channel, device, _fileTransfer, _clipboard, () => DownloadDirectory);
            session.Ended += OnSessionEnded;
            if (previous is not null) await previous.DisposeAsync(); // ends it (and removes it) before the new one is listed
            lock (_gate)
            {
                if (previous is not null) _sessions.Remove(previous);
                _sessions.Add(session);
            }
            SessionStarted?.Invoke(session);
            session.Start();
            RaiseActiveChanged();
        }
        finally
        {
            _sessionLock.Release();
        }
        return true;
    }

    private void OnSessionEnded(BridgeSession session)
    {
        lock (_gate) _sessions.Remove(session);
        SessionEnded?.Invoke(session);
        RaiseActiveChanged();
    }

    /// <summary>Forgets a paired device and drops its connection (docs/security.md, Revocation).</summary>
    public async Task RevokeAsync(string deviceId)
    {
        await _store.RevokeAsync(deviceId);
        lock (_gate) { if (_activeDeviceId == deviceId) _activeDeviceId = null; }
        if (SessionFor(deviceId) is { } session) await session.DisposeAsync();
    }

    private static async Task SendErrorAsync(EncryptedChannel channel, string code, string message, CancellationToken ct)
    {
        try { await channel.SendAsync(BridgeMessage.Create(MessageType.Error, new ErrorPayload(code, message)), ct); }
        catch { /* best effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var session in Sessions) await session.DisposeAsync();
    }
}
