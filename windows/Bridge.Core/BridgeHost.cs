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
/// </summary>
public sealed class BridgeHost : IAsyncDisposable
{
    public static readonly string[] LocalFeatures = [Features.Files, Features.Clipboard, Features.Notifications];
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan FirstMessageTimeout = TimeSpan.FromSeconds(30);

    private readonly DeviceIdentity _identity;
    private readonly IPairedDeviceStore _store;
    private readonly PairingService _pairing;
    private readonly FileTransferService _fileTransfer;
    private readonly ClipboardSyncService _clipboard;
    private readonly string _downloadDir;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private BridgeSession? _current;

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

    public BridgeSession? CurrentSession => Volatile.Read(ref _current);

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
        await channel.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload(LocalFeatures)), ct);

        var session = new BridgeSession(channel, device, _fileTransfer, _clipboard, _downloadDir);
        session.Ended += OnSessionEnded;

        await _sessionLock.WaitAsync(ct);
        try
        {
            // One phone at a time: a reconnect replaces a stale (half-open) session.
            var previous = Interlocked.Exchange(ref _current, session);
            if (previous is not null) await previous.DisposeAsync();
            SessionStarted?.Invoke(session);
            session.Start();
        }
        finally
        {
            _sessionLock.Release();
        }
        return true;
    }

    private void OnSessionEnded(BridgeSession session)
    {
        Interlocked.CompareExchange(ref _current, null, session);
        SessionEnded?.Invoke(session);
    }

    /// <summary>Forgets a paired device and drops its connection (docs/security.md, Revocation).</summary>
    public async Task RevokeAsync(string deviceId)
    {
        await _store.RevokeAsync(deviceId);
        var session = CurrentSession;
        if (session is not null && session.Device.DeviceId == deviceId) await session.DisposeAsync();
    }

    private static async Task SendErrorAsync(EncryptedChannel channel, string code, string message, CancellationToken ct)
    {
        try { await channel.SendAsync(BridgeMessage.Create(MessageType.Error, new ErrorPayload(code, message)), ct); }
        catch { /* best effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        var session = Interlocked.Exchange(ref _current, null);
        if (session is not null) await session.DisposeAsync();
    }
}
