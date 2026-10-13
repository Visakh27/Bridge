using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bridge.Protocol;
using Bridge.Security;

namespace Bridge.Network;

/// <summary>
/// Local discovery wire format (protocol/protocol.md, "Discovery"). A paired
/// phone that can't reach the PC at its stored address broadcasts a probe; only
/// the PC whose identity key matches the probe's tag answers, with its current
/// TCP port signed by that key. Nothing here identifies a PC to an observer who
/// doesn't already know its public key.
/// </summary>
public static class DiscoveryWire
{
    public const int DefaultPort = 47821;
    public const int MaxDatagram = 512;
    public const int NonceSize = 16;
    public const int TagSize = 16;
    public const string ProbeKind = "discover";
    public const string ReplyKind = "here";
    public const int Version = 1;

    public sealed record Probe(string Bridge, int V, string Nonce, string Tag);
    public sealed record Reply(string Bridge, int V, string Nonce, string DeviceId, int Port, string Sig);

    /// <summary>First 16 bytes of SHA-256( f("bridge-discovery-tag-v1") ‖ f(pcKey) ‖ f(nonce) ).</summary>
    public static byte[] Tag(byte[] pcPublicKey, byte[] nonce) =>
        SHA256.HashData(Fields(Encoding.UTF8.GetBytes("bridge-discovery-tag-v1"), pcPublicKey, nonce))[..TagSize];

    /// <summary>Bytes the PC signs (ECDSA-SHA256): f("bridge-discovery-v1") ‖ f(nonce) ‖ f(deviceId) ‖ f(port as 4-byte BE).</summary>
    public static byte[] SignedData(byte[] nonce, string deviceId, int port)
    {
        var portBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(portBytes, port);
        return Fields(Encoding.UTF8.GetBytes("bridge-discovery-v1"), nonce, Encoding.UTF8.GetBytes(deviceId), portBytes);
    }

    public static byte[] BuildProbe(byte[] pcPublicKey, byte[] nonce) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
        new Probe(ProbeKind, Version, Convert.ToBase64String(nonce), Convert.ToBase64String(Tag(pcPublicKey, nonce))), BridgeJson.Options));

    /// <summary>Phone side: returns the PC's TCP port if <paramref name="datagram"/> is a genuine reply to <paramref name="nonce"/>.</summary>
    public static int? VerifyReply(byte[] datagram, byte[] nonce, string expectedDeviceId, byte[] pcPublicKey)
    {
        try
        {
            if (datagram.Length > MaxDatagram) return null;
            var reply = JsonSerializer.Deserialize<Reply>(datagram, BridgeJson.Options);
            if (reply is null || reply.Bridge != ReplyKind || reply.V != Version) return null;
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(reply.Nonce), nonce)) return null;
            if (reply.DeviceId != expectedDeviceId || reply.Port is < 1 or > 65535) return null;
            var ok = DeviceIdentity.Verify(pcPublicKey, SignedData(nonce, reply.DeviceId, reply.Port), Convert.FromBase64String(reply.Sig));
            return ok ? reply.Port : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] Fields(params byte[][] values)
    {
        using var ms = new MemoryStream();
        Span<byte> len = stackalloc byte[4];
        foreach (var v in values)
        {
            BinaryPrimitives.WriteInt32BigEndian(len, v.Length);
            ms.Write(len);
            ms.Write(v);
        }
        return ms.ToArray();
    }
}

/// <summary>
/// PC side of discovery: listens on UDP 47821 and answers probes meant for this
/// PC (tag matches our identity key) with our current TCP port. Replies are
/// rate-limited, and anything malformed or meant for another PC is ignored
/// silently.
/// </summary>
public sealed class DiscoveryResponder : IDisposable
{
    public const int MaxRepliesPerSecond = 20;

    private readonly DeviceIdentity _identity;
    private readonly Func<int> _tcpPort;
    private readonly TimeProvider _time;
    private readonly UdpClient _udp;
    private readonly object _gate = new();
    private double _tokens = MaxRepliesPerSecond;
    private DateTimeOffset _lastRefill;

    public int Port { get; }

    /// <summary>Raised for each probe answered (diagnostics; no identifying data).</summary>
    public event Action? Answered;

    /// <param name="tcpPort">Current TCP listen port (read at reply time).</param>
    /// <param name="udpPort">UDP port to bind; 0 picks any free port (tests).</param>
    /// <exception cref="SocketException">The UDP port is unavailable.</exception>
    public DiscoveryResponder(DeviceIdentity identity, Func<int> tcpPort, int udpPort = DiscoveryWire.DefaultPort, TimeProvider? time = null)
    {
        _identity = identity;
        _tcpPort = tcpPort;
        _time = time ?? TimeProvider.System;
        _lastRefill = _time.GetUtcNow();
        _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        if (OperatingSystem.IsWindows())
        {
            // Without this, an ICMP "port unreachable" for an earlier reply (the
            // phone already closed its socket) makes the next receive fail with
            // WSAECONNRESET on Windows.
            const int SIO_UDP_CONNRESET = -1744830452;
            _udp.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    }

    /// <summary>Receive loop; runs until cancelled or disposed. Never throws.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await _udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; } // e.g. ICMP port-unreachable from an earlier reply (Windows)

            var reply = HandleDatagram(datagram.Buffer);
            if (reply is null) continue;
            try
            {
                await _udp.SendAsync(reply, datagram.RemoteEndPoint, ct);
                Answered?.Invoke();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    /// <summary>Returns the reply bytes for a probe addressed to this PC, otherwise null.</summary>
    public byte[]? HandleDatagram(byte[] datagram)
    {
        if (datagram.Length > DiscoveryWire.MaxDatagram) return null;
        byte[] nonce, tag;
        try
        {
            var probe = JsonSerializer.Deserialize<DiscoveryWire.Probe>(datagram, BridgeJson.Options);
            if (probe is null || probe.Bridge != DiscoveryWire.ProbeKind || probe.V != DiscoveryWire.Version) return null;
            nonce = Convert.FromBase64String(probe.Nonce);
            tag = Convert.FromBase64String(probe.Tag);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
        if (nonce.Length != DiscoveryWire.NonceSize || tag.Length != DiscoveryWire.TagSize) return null;
        if (!CryptographicOperations.FixedTimeEquals(tag, DiscoveryWire.Tag(_identity.PublicKeyBlob, nonce))) return null;
        if (!TakeToken()) return null;

        var port = _tcpPort();
        var sig = _identity.Sign(DiscoveryWire.SignedData(nonce, _identity.DeviceId, port));
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new DiscoveryWire.Reply(
            DiscoveryWire.ReplyKind, DiscoveryWire.Version, Convert.ToBase64String(nonce), _identity.DeviceId, port,
            Convert.ToBase64String(sig)), BridgeJson.Options));
    }

    private bool TakeToken()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _tokens = Math.Min(MaxRepliesPerSecond, _tokens + (now - _lastRefill).TotalSeconds * MaxRepliesPerSecond);
            _lastRefill = now;
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }

    public void Dispose() => _udp.Dispose();
}
