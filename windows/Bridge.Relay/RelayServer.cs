using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Bridge.Network;

namespace Bridge.Relay;

/// <summary>Limits for one relay. The defaults suit a small server shared by a household or team.</summary>
public sealed record RelayOptions
{
    public int MaxConnections { get; init; } = 512;
    public int MaxConnectionsPerAddress { get; init; } = 32;
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>A waiting PC gets a ping this often, so NATs and the PC both see the line is alive.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(25);
    /// <summary>A pipe with no bytes either way for this long is closed (Bridge itself pings every 15 s).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public int MaxRooms { get; init; } = 10_000;
}

/// <summary>
/// The self-hosted relay (docs/remote.md). It knows nothing about Bridge's messages: a PC joins a
/// room and waits; when the phone for that room joins, the two connections are piped together and
/// Bridge's own end-to-end handshake and encryption run through. The relay checks only that each
/// client holds the relay key (<see cref="RelayProtocol.Verify"/>, each proof usable once) and
/// enforces its limits. It logs counts and reasons, never rooms, addresses or bytes.
/// </summary>
public sealed class RelayServer : IAsyncDisposable
{
    private readonly byte[] _key;
    private readonly RelayOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Waiting> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<IPAddress, int> _perAddress = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nonces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<TcpClient, byte> _open = new();
    private int _connections;
    private int _pipes;

    private sealed class Waiting(TcpClient pc)
    {
        public TcpClient Pc { get; } = pc;
        public TaskCompletionSource<TcpClient> Phone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Completes when the pipe the PC side runs has ended.</summary>
        public TaskCompletionSource PipeDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public RelayServer(byte[] key, RelayOptions? options = null, Func<DateTimeOffset>? now = null)
    {
        if (key.Length != RelayLink.KeyLength) throw new ArgumentException("The relay key must be 32 bytes", nameof(key));
        _key = key;
        _options = options ?? new RelayOptions();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>(event, detail): counts and reason codes only.</summary>
    public event Action<string, string>? Log;

    public int Connections => Volatile.Read(ref _connections);
    public int WaitingPcs { get { lock (_gate) return _rooms.Count; } }
    public int Pipes => Volatile.Read(ref _pipes);

    public async Task RunAsync(TcpListener listener, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        listener.Start();
        Log?.Invoke("relay.started", $"port={((IPEndPoint)listener.LocalEndpoint).Port}");
        try
        {
            while (!linked.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(linked.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { await Task.Delay(100, CancellationToken.None); continue; }
                _ = HandleAsync(client, linked.Token); // never throws
            }
        }
        finally { listener.Stop(); }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        bool admitted;
        lock (_gate)
        {
            _perAddress.TryGetValue(address, out var fromHere);
            admitted = _connections < _options.MaxConnections && fromHere < _options.MaxConnectionsPerAddress;
            if (admitted) { _connections++; _perAddress[address] = fromHere + 1; }
        }
        _open[client] = 0;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            if (!admitted)
            {
                Log?.Invoke("relay.refused", RelayError.Busy);
                await TrySend(stream, RelayProtocol.Line("error", RelayError.Busy), ct);
                return;
            }
            string? line;
            using (var hello = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                hello.CancelAfter(_options.HelloTimeout);
                try { line = await RelayProtocol.ReadLineAsync(stream, hello.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException) { line = null; }
            }
            var parsed = line is null ? null : RelayProtocol.ParseHello(line);
            if (parsed is null)
            {
                Log?.Invoke("relay.refused", RelayError.BadHello);
                await TrySend(stream, RelayProtocol.Line("error", RelayError.BadHello), ct);
                return;
            }
            if (!RelayProtocol.Verify(_key, parsed, _now()) || !FreshNonce(parsed.Nonce))
            {
                Log?.Invoke("relay.refused", RelayError.Unauthorized);
                await TrySend(stream, RelayProtocol.Line("error", RelayError.Unauthorized), ct);
                return;
            }
            if (parsed.Role == RelayRole.Pc) await ServePcAsync(client, stream, parsed.Room, ct);
            else await ServePhoneAsync(client, stream, parsed.Room, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // The client went away; nothing to tell anyone.
        }
        finally
        {
            _open.TryRemove(client, out _);
            client.Dispose();
            lock (_gate)
            {
                if (admitted)
                {
                    _connections--;
                    if (_perAddress.TryGetValue(address, out var n) && n > 1) _perAddress[address] = n - 1; else _perAddress.Remove(address);
                }
            }
        }
    }

    /// <summary>Each proof works once (within the clock-skew window it could otherwise be replayed).</summary>
    private bool FreshNonce(string nonce)
    {
        var now = _now();
        if (_nonces.Count > 50_000)
            foreach (var old in _nonces.Where(n => n.Value < now).Select(n => n.Key).ToList()) _nonces.TryRemove(old, out _);
        return _nonces.TryAdd(nonce, now + RelayProtocol.MaxClockSkew * 2);
    }

    private async Task ServePcAsync(TcpClient pc, NetworkStream stream, string room, CancellationToken ct)
    {
        var waiting = new Waiting(pc);
        Waiting? replaced;
        bool full;
        lock (_gate)
        {
            _rooms.TryGetValue(room, out replaced);
            full = replaced is null && _rooms.Count >= _options.MaxRooms;
            if (!full) _rooms[room] = waiting;
        }
        if (full)
        {
            Log?.Invoke("relay.refused", RelayError.Busy);
            await TrySend(stream, RelayProtocol.Line("error", RelayError.Busy), ct);
            return;
        }
        // The same PC dialing again (its old connection died unnoticed): the newest one waits.
        replaced?.Phone.TrySetException(new RelayRefusedException(RelayError.Replaced));
        using var leave = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await RelayProtocol.WriteLineAsync(stream, RelayProtocol.Line("waiting"), ct);
            // A waiting PC sends nothing (the phone starts the handshake), so a read that finishes means it
            // hung up — noticed at once instead of at the next ping. A byte it reads after pairing is the
            // start of the PC's reply: the pipe forwards it first.
            var first = new byte[1];
            var pcRead = stream.ReadAsync(first, leave.Token).AsTask();
            TcpClient phone;
            while (true)
            {
                var winner = await Task.WhenAny(waiting.Phone.Task, pcRead, Task.Delay(_options.PingInterval, ct));
                if (winner == waiting.Phone.Task) break;
                if (winner == pcRead) return; // hung up (or broke the protocol): its place is freed
                ct.ThrowIfCancellationRequested();
                await RelayProtocol.WriteLineAsync(stream, RelayProtocol.Line("ping"), ct);
            }
            try { phone = await waiting.Phone.Task; }
            catch (RelayRefusedException) { await TrySend(stream, RelayProtocol.Line("error", RelayError.Replaced), ct); return; }
            await RelayProtocol.WriteLineAsync(stream, RelayProtocol.Line("paired"), ct);
            Interlocked.Increment(ref _pipes);
            Log?.Invoke("relay.paired", $"pipes={Pipes} waiting={WaitingPcs}");
            try { await PipeAsync(pc, phone, ct, pcRead, first); }
            finally
            {
                Interlocked.Decrement(ref _pipes);
                waiting.PipeDone.TrySetResult();
            }
        }
        finally
        {
            leave.Cancel();
            lock (_gate) { if (_rooms.TryGetValue(room, out var w) && ReferenceEquals(w, waiting)) _rooms.Remove(room); }
            waiting.Phone.TrySetException(new IOException("PC left")); // a phone that raced in gets nothing
            waiting.PipeDone.TrySetResult();
        }
    }

    private async Task ServePhoneAsync(TcpClient phone, NetworkStream stream, string room, CancellationToken ct)
    {
        Waiting? waiting;
        lock (_gate) { if (_rooms.Remove(room, out waiting) && waiting.Phone.Task.IsCompleted) waiting = null; }
        if (waiting is null)
        {
            Log?.Invoke("relay.refused", RelayError.PcOffline);
            await TrySend(stream, RelayProtocol.Line("error", RelayError.PcOffline), ct);
            return;
        }
        await RelayProtocol.WriteLineAsync(stream, RelayProtocol.Line("paired"), ct);
        if (!waiting.Phone.TrySetResult(phone)) return; // the PC left at that very moment
        await waiting.PipeDone.Task.WaitAsync(ct); // the PC side runs the pipe; keep this connection counted until it ends
    }

    /// <summary>
    /// Copies bytes both ways until either side closes or the line is idle for too long.
    /// <paramref name="aFirstRead"/> is a read already pending on <paramref name="a"/> into <paramref name="aFirst"/>.
    /// </summary>
    private async Task PipeAsync(TcpClient a, TcpClient b, CancellationToken ct, Task<int> aFirstRead, byte[] aFirst)
    {
        using var pipe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var last = Environment.TickCount64;
        async Task Copy(NetworkStream from, NetworkStream to)
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await from.ReadAsync(buffer, pipe.Token);
                if (read == 0) return;
                Volatile.Write(ref last, Environment.TickCount64);
                await to.WriteAsync(buffer.AsMemory(0, read), pipe.Token);
            }
        }
        var idle = Task.Run(async () =>
        {
            while (!pipe.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, _options.IdleTimeout.TotalMilliseconds / 4)), pipe.Token);
                if (Environment.TickCount64 - Volatile.Read(ref last) > _options.IdleTimeout.TotalMilliseconds)
                {
                    Log?.Invoke("relay.idle", "closed");
                    return;
                }
            }
        }, CancellationToken.None);
        async Task CopyFromA()
        {
            if (await aFirstRead == 0) return;
            Volatile.Write(ref last, Environment.TickCount64);
            await b.GetStream().WriteAsync(aFirst, pipe.Token);
            await Copy(a.GetStream(), b.GetStream());
        }
        try
        {
            await Task.WhenAny(CopyFromA(), Copy(b.GetStream(), a.GetStream()), idle);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            pipe.Cancel();
            a.Close();
            b.Close();
        }
    }

    private static async Task TrySend(Stream stream, string line, CancellationToken ct)
    {
        try { await RelayProtocol.WriteLineAsync(stream, line, ct); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var c in _open.Keys) { try { c.Close(); } catch (Exception) { } }
        return ValueTask.CompletedTask;
    }
}
