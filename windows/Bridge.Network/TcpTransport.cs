using System.Net.Sockets;

namespace Bridge.Network;

/// <summary>
/// Length-prefixed framing over a raw TCP socket: [4-byte big-endian length][payload].
/// Used for both the plaintext handshake bytes and, afterwards, encrypted
/// session frames — the caller decides what goes in `data`.
/// </summary>
public sealed class TcpTransport : ITransport
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Largest frame ever accepted (after authentication).</summary>
    public const int MaxSessionFrameSize = 16 * 1024 * 1024;

    public int MaxFrameSize { get; set; } = MaxSessionFrameSize;

    public TcpTransport(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
    }

    public static async Task<TcpTransport> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, ct);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return new TcpTransport(client);
    }

    public bool IsConnected => _client.Connected;

    public async Task SendFrameAsync(byte[] data, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var lenPrefix = BitConverter.GetBytes(data.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(lenPrefix);
            await _stream.WriteAsync(lenPrefix, ct);
            await _stream.WriteAsync(data, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<byte[]> ReceiveFrameAsync(CancellationToken ct = default)
    {
        var lenBuf = await ReadExactAsync(4, ct);
        if (BitConverter.IsLittleEndian) Array.Reverse(lenBuf);
        var len = BitConverter.ToInt32(lenBuf, 0);
        if (len < 0 || len > MaxFrameSize)
            throw new InvalidDataException($"Refusing to read frame of size {len}");
        return await ReadExactAsync(len, ct);
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0) throw new IOException("Connection closed mid-frame");
            offset += read;
        }
        return buffer;
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _client.Dispose();
    }
}

/// <summary>Listens for incoming pairing/reconnect TCP connections on the local network.</summary>
public sealed class TcpBridgeListener : IDisposable
{
    /// <summary>
    /// Stable default port. Paired phones reconnect to the host/port they saw in
    /// the QR code, so a port that changed on every launch would strand them.
    /// </summary>
    public const int DefaultPort = 47820;

    private readonly TcpListener _listener;
    public int Port { get; }

    /// <param name="port">Port to bind; 0 picks any free port.</param>
    /// <param name="fallbackToEphemeral">If the requested port is taken, bind a free port instead of failing.</param>
    public TcpBridgeListener(int port = DefaultPort, bool fallbackToEphemeral = true)
    {
        try
        {
            _listener = Start(port);
        }
        catch (SocketException) when (fallbackToEphemeral && port != 0)
        {
            _listener = Start(0);
        }
        Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
    }

    private static TcpListener Start(int port)
    {
        var listener = new TcpListener(System.Net.IPAddress.Any, port);
        listener.Start();
        return listener;
    }

    public async Task<TcpTransport> AcceptAsync(CancellationToken ct = default)
    {
        var client = await _listener.AcceptTcpClientAsync(ct);
        return new TcpTransport(client);
    }

    public void Dispose() => _listener.Stop();
}
