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

    public TcpTransport(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<TcpTransport> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
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
        if (len is < 0 or > 64 * 1024 * 1024)
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
    private readonly TcpListener _listener;
    public int Port { get; }

    public TcpBridgeListener(int port = 0)
    {
        _listener = new TcpListener(System.Net.IPAddress.Any, port);
        _listener.Start();
        Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public async Task<TcpTransport> AcceptAsync(CancellationToken ct = default)
    {
        var client = await _listener.AcceptTcpClientAsync(ct);
        return new TcpTransport(client);
    }

    public void Dispose() => _listener.Stop();
}
