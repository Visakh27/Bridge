using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;

namespace Bridge.Core;

public enum RemoteState
{
    /// <summary>Remote access is off (the default).</summary>
    Off,
    /// <summary>Reaching the relay.</summary>
    Connecting,
    /// <summary>Waiting at the relay: paired phones can reach this PC from anywhere.</summary>
    Ready,
    /// <summary>The relay can't be reached right now; retrying.</summary>
    Unreachable,
    /// <summary>The relay refused the key (wrong link?) or is full; retrying slowly.</summary>
    Refused,
}

/// <summary>Remote access as the Settings panel shows it. <see cref="Code"/>: the relay's reason when refused.</summary>
public sealed record RemoteStatus(RemoteState State, string? Code = null)
{
    public string Describe(string? relay) => State switch
    {
        RemoteState.Off => "Off. Your phone connects only on the same network as this PC.",
        RemoteState.Connecting => $"Connecting to {relay}…",
        RemoteState.Ready => $"On. Your phones can reach this PC through {relay} when they're away.",
        RemoteState.Unreachable => $"Can't reach {relay}. Bridge keeps trying; local connections still work.",
        RemoteState.Refused when Code == RelayError.Unauthorized => $"{relay} didn't accept the key. Check the relay link.",
        RemoteState.Refused => $"{relay} is busy. Bridge keeps trying.",
        _ => "",
    };
}

/// <summary>
/// Remote access through the user's relay (opt-in). For every paired phone this keeps one
/// connection waiting at the relay in that phone's room; when the phone joins, the connection
/// becomes an ordinary incoming connection for <see cref="BridgeHost"/>, which runs the same
/// mutual authentication and encryption as on the local network (pairing is refused through
/// the relay). It also tells phones that support it where the relay is (relay.config), over
/// their authenticated session, so a phone learns it while at home.
/// </summary>
public sealed class RemoteAccess : IAsyncDisposable
{
    public static readonly TimeSpan[] DefaultBackoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];
    public static readonly TimeSpan RefusedBackoff = TimeSpan.FromMinutes(2);

    private readonly BridgeHost _host;
    private readonly IPairedDeviceStore _store;
    private readonly DeviceIdentity _identity;
    private readonly TimeSpan[] _backoff;
    private readonly TimeSpan _refusedBackoff;
    private readonly TimeSpan _rescan;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _life = new();
    private readonly Dictionary<string, Task> _loops = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteState> _loopState = new(StringComparer.Ordinal);
    private CancellationTokenSource? _run;
    private RelayLink? _link;
    private RemoteStatus _status = new(RemoteState.Off);
    private string? _lastRefusal;
    private Task? _supervisor;

    public RemoteAccess(BridgeHost host, IPairedDeviceStore store, DeviceIdentity identity,
        TimeSpan[]? backoff = null, TimeSpan? refusedBackoff = null, TimeSpan? rescan = null, Func<DateTimeOffset>? now = null)
    {
        _host = host;
        _store = store;
        _identity = identity;
        _backoff = backoff ?? DefaultBackoff;
        _refusedBackoff = refusedBackoff ?? RefusedBackoff;
        _rescan = rescan ?? TimeSpan.FromSeconds(3);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        host.SessionStarted += session =>
        {
            if (session.Device.PeerFeatures.Contains(Features.Remote)) _ = SendConfigAsync(session, Link);
        };
    }

    public RelayLink? Link { get { lock (_gate) return _link; } }
    public RemoteStatus Status { get { lock (_gate) return _status; } }

    public event Action<RemoteStatus>? StatusChanged;
    /// <summary>Diagnostics: (event, code) — never the relay's address, the key or rooms.</summary>
    public event Action<string, string>? Completed;

    /// <summary>
    /// Turns remote access on with <paramref name="link"/>, or off (null). Restarts the waiting connections,
    /// drops sessions that came through the old relay, and tells connected phones.
    /// </summary>
    public async Task ConfigureAsync(RelayLink? link)
    {
        CancellationTokenSource? old;
        Task? oldSupervisor;
        lock (_gate)
        {
            if (Equals(_link, link) && (link is null || _run is not null)) return;
            _link = link;
            old = _run;
            oldSupervisor = _supervisor;
            _run = link is null ? null : CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            _loops.Clear();
            _loopState.Clear();
            _lastRefusal = null;
        }
        old?.Cancel();
        if (oldSupervisor is not null) { try { await oldSupervisor; } catch (OperationCanceledException) { } }
        old?.Dispose();
        // Phones connected through the previous relay lose it.
        foreach (var s in _host.Sessions.Where(s => s.Device.Route == ConnectionRoute.Relay)) await s.DisposeAsync();
        foreach (var s in _host.Sessions.Where(s => s.Device.PeerFeatures.Contains(Features.Remote))) await SendConfigAsync(s, link);
        Completed?.Invoke("remote.configured", link is null ? "off" : "on");
        UpdateStatus();
        lock (_gate) { if (_run is { } run && link is not null) _supervisor = Task.Run(() => SuperviseAsync(link, run.Token)); else _supervisor = null; }
    }

    private static async Task SendConfigAsync(BridgeSession session, RelayLink? link)
    {
        var payload = link is null
            ? new RelayConfigPayload(false)
            : new RelayConfigPayload(true) { Host = link.Host, Port = link.Port, Key = RelayLink.EncodeKey(link.Key) };
        try { await session.SendAsync(BridgeMessage.Create(MessageType.RelayConfig, payload)); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>Keeps one waiting loop per paired phone (phones paired or forgotten later are picked up).</summary>
    private async Task SuperviseAsync(RelayLink link, CancellationToken ct)
    {
        var rescan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPaired(PairedDevice _) => rescan.TrySetResult();
        _host.DevicePaired += OnPaired;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<PairedDevice> paired;
                try { paired = await _store.ListAsync(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { paired = []; }
                lock (_gate)
                {
                    foreach (var device in paired)
                        if (!_loops.TryGetValue(device.DeviceId, out var running) || running.IsCompleted)
                        {
                            _loopState[device.DeviceId] = RemoteState.Connecting;
                            _loops[device.DeviceId] = Task.Run(() => WaitLoopAsync(link, device, ct));
                        }
                    // Forgotten phones: their loops notice on their next round (they check the store).
                    foreach (var gone in _loopState.Keys.Where(id => paired.All(d => d.DeviceId != id)).ToList()) _loopState.Remove(gone);
                }
                UpdateStatus();
                rescan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await Task.WhenAny(rescan.Task, Task.Delay(_rescan, ct));
            }
        }
        catch (OperationCanceledException) { }
        finally { _host.DevicePaired -= OnPaired; }
    }

    private async Task WaitLoopAsync(RelayLink link, PairedDevice device, CancellationToken ct)
    {
        var room = RelayProtocol.Room(_identity.PublicKeyBlob, device.LongTermPublicKey);
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if ((await _store.ListAsync()).All(d => d.DeviceId != device.DeviceId)) return; // forgotten
                var client = await RelayClient.JoinAsync(link, RelayRole.Pc, room, ct, _now, waiting: () =>
                {
                    attempt = 0;
                    SetLoopState(device.DeviceId, RemoteState.Ready);
                });
                Completed?.Invoke("remote.joined", "phone");
                // An ordinary incoming connection from here on: authenticated like any other.
                _ = _host.HandleConnectionAsync(new TcpTransport(client), _life.Token, ConnectionRoute.Relay);
                continue; // wait for the next time at once
            }
            catch (OperationCanceledException) { return; }
            catch (RelayRefusedException ex) when (ex.Code == RelayError.Replaced)
            {
                // An older connection of ours: the newer one is waiting. (Pause anyway, in case
                // another copy of this PC keeps taking the place.)
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            catch (RelayRefusedException ex)
            {
                lock (_gate) _lastRefusal = ex.Code;
                SetLoopState(device.DeviceId, RemoteState.Refused);
                Completed?.Invoke("remote.refused", ex.Code);
                try { await Task.Delay(_refusedBackoff, ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidDataException)
            {
                SetLoopState(device.DeviceId, RemoteState.Unreachable);
                Completed?.Invoke("remote.unreachable", ex is System.Net.Sockets.SocketException se ? se.SocketErrorCode.ToString() : ex.GetType().Name);
            }
            var wait = _backoff[Math.Min(attempt++, _backoff.Length - 1)];
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void SetLoopState(string deviceId, RemoteState state)
    {
        lock (_gate) { if (_link is not null) _loopState[deviceId] = state; }
        UpdateStatus();
    }

    /// <summary>Ready if any phone's connection is waiting; else the most telling problem.</summary>
    private void UpdateStatus()
    {
        RemoteStatus next;
        lock (_gate)
        {
            var states = _loopState.Values.ToList();
            next = _link is null ? new RemoteStatus(RemoteState.Off)
                : states.Contains(RemoteState.Ready) ? new RemoteStatus(RemoteState.Ready)
                : states.Contains(RemoteState.Refused) ? new RemoteStatus(RemoteState.Refused, _lastRefusal)
                : states.Contains(RemoteState.Unreachable) ? new RemoteStatus(RemoteState.Unreachable)
                : new RemoteStatus(RemoteState.Connecting);
            if (next == _status) return;
            _status = next;
        }
        StatusChanged?.Invoke(next);
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        Task? supervisor;
        lock (_gate) supervisor = _supervisor;
        if (supervisor is not null) { try { await supervisor; } catch (OperationCanceledException) { } }
    }
}
