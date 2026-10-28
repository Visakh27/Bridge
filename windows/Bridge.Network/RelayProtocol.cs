using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bridge.Network;

/// <summary>
/// Where a self-hosted relay is and the key that lets Bridge use it, written as one link:
/// <c>bridge-relay://relay.example.com:47830#&lt;key&gt;</c> (key: 32 bytes, base64url).
/// The key never travels to the relay: clients prove they hold it (<see cref="RelayProtocol.Proof"/>).
/// </summary>
public sealed record RelayLink(string Host, int Port, byte[] Key)
{
    public const string Scheme = "bridge-relay://";
    public const int DefaultPort = 47830;
    public const int KeyLength = 32;

    private static readonly Regex HostName = new(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$", RegexOptions.CultureInvariant);

    /// <summary>Parses a relay link; null if anything about it is off.</summary>
    public static RelayLink? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.Length > 400 || !text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = text[Scheme.Length..];
        var hash = rest.IndexOf('#');
        if (hash < 0) return null;
        var authority = rest[..hash].TrimEnd('/');
        var key = DecodeKey(rest[(hash + 1)..]);
        if (key is null) return null;
        string host;
        var port = DefaultPort;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) return null;
            host = authority[1..close];
            var after = authority[(close + 1)..];
            if (after.Length > 0 && !TryPort(after, out port)) return null;
            if (!System.Net.IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) return null;
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            if (colon >= 0 && !TryPort(authority[colon..], out port)) return null;
            if (!HostName.IsMatch(host)) return null; // also covers dotted IPv4
        }
        return new RelayLink(host.ToLowerInvariant(), port, key);
    }

    private static bool TryPort(string colonPort, out int port)
    {
        port = 0;
        return colonPort.Length is > 1 and <= 6 && colonPort[0] == ':' && colonPort[1..].All(char.IsAsciiDigit) &&
               int.TryParse(colonPort[1..], NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
    }

    public static byte[]? DecodeKey(string? text)
    {
        if (text is null || text.Length != 43 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        try
        {
            var bytes = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + "=");
            return bytes.Length == KeyLength ? bytes : null;
        }
        catch (FormatException) { return null; }
    }

    public static string EncodeKey(byte[] key) => Convert.ToBase64String(key).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyLength);

    public override string ToString() =>
        $"{Scheme}{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port.ToString(CultureInfo.InvariantCulture)}#{EncodeKey(Key)}";

    /// <summary>Host and port only, for showing on screen (never the key).</summary>
    public string Display => $"{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port.ToString(CultureInfo.InvariantCulture)}";

    public bool Equals(RelayLink? other) => other is not null && Host == other.Host && Port == other.Port && Key.AsSpan().SequenceEqual(other.Key);
    public override int GetHashCode() => HashCode.Combine(Host, Port);
}

/// <summary>Who is joining the relay.</summary>
public static class RelayRole
{
    public const string Pc = "pc";
    public const string Phone = "phone";
}

/// <summary>Why the relay turned a client away (the "code" of an error line).</summary>
public static class RelayError
{
    public const string BadHello = "bad_hello";
    public const string Unauthorized = "unauthorized";
    public const string PcOffline = "pc_offline";
    public const string Busy = "busy";
    public const string Replaced = "replaced";
}

/// <summary>The relay refused or dropped the join (see <see cref="RelayError"/>).</summary>
public sealed class RelayRefusedException(string code) : IOException($"Relay refused: {code}")
{
    public string Code { get; } = code;
}

/// <summary>
/// The relay's tiny join protocol, shared by the PC, the relay and (in Kotlin) the phone.
/// A client sends one JSON line:
/// <c>{"v":1,"role":"pc|phone","room":"&lt;64 hex&gt;","ts":&lt;unix s&gt;,"nonce":"&lt;32 hex&gt;","proof":"&lt;64 hex&gt;"}</c>.
/// The relay answers with lines: <c>{"t":"waiting"}</c> and <c>{"t":"ping"}</c> (PC, while it waits),
/// <c>{"t":"paired"}</c> — after which the connection is a plain byte pipe to the other side — or
/// <c>{"t":"error","code":"…"}</c> and closes. Bridge's own handshake then runs end to end through
/// the pipe, so the relay never sees keys or contents.
/// </summary>
public static class RelayProtocol
{
    public const int Version = 1;
    public const int MaxLine = 1024;
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);
    private static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Hex32 = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The meeting point for one PC and one phone: SHA-256 over both long-term public keys exactly as
    /// exchanged at pairing. Only those two devices know both keys.
    /// </summary>
    public static string Room(byte[] pcPublicKey, byte[] phonePublicKey)
    {
        var prefix = Encoding.ASCII.GetBytes("bridge-relay-room-v1");
        var data = new byte[prefix.Length + 1 + pcPublicKey.Length + 1 + phonePublicKey.Length];
        prefix.CopyTo(data, 0);
        pcPublicKey.CopyTo(data, prefix.Length + 1);
        phonePublicKey.CopyTo(data, prefix.Length + 2 + pcPublicKey.Length);
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    /// <summary>HMAC-SHA256 with the relay key over the hello's fields; proves the key without sending it.</summary>
    public static string Proof(byte[] key, string role, string room, long ts, string nonce) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"bridge-relay-v1\n{role}\n{room}\n{ts.ToString(CultureInfo.InvariantCulture)}\n{nonce}"))).ToLowerInvariant();

    public static string Hello(byte[] key, string role, string room, DateTimeOffset now, string? nonce = null)
    {
        nonce ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var ts = now.ToUnixTimeSeconds();
        return new JsonObject
        {
            ["v"] = Version, ["role"] = role, ["room"] = room, ["ts"] = ts, ["nonce"] = nonce, ["proof"] = Proof(key, role, room, ts, nonce),
        }.ToJsonString();
    }

    public sealed record ParsedHello(string Role, string Room, long Ts, string Nonce, string Proof);

    /// <summary>Checks a hello's shape (not its proof); null if malformed.</summary>
    public static ParsedHello? ParseHello(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o || o.Count != 6) return null;
            if (o["v"]?.GetValue<int>() != Version) return null;
            var role = o["role"]?.GetValue<string>();
            var room = o["room"]?.GetValue<string>();
            var nonce = o["nonce"]?.GetValue<string>();
            var proof = o["proof"]?.GetValue<string>();
            var ts = o["ts"]?.GetValue<long>() ?? 0;
            if (role is not (RelayRole.Pc or RelayRole.Phone) || room is null || !Hex64.IsMatch(room) ||
                nonce is null || !Hex32.IsMatch(nonce) || proof is null || !Hex64.IsMatch(proof) || ts <= 0) return null;
            return new ParsedHello(role, room, ts, nonce, proof);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>Whether <paramref name="hello"/> carries a valid proof made within <see cref="MaxClockSkew"/> of <paramref name="now"/>.</summary>
    public static bool Verify(byte[] key, ParsedHello hello, DateTimeOffset now)
    {
        if (Math.Abs(now.ToUnixTimeSeconds() - hello.Ts) > MaxClockSkew.TotalSeconds) return false;
        var expected = Convert.FromHexString(Proof(key, hello.Role, hello.Room, hello.Ts, hello.Nonce));
        return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(hello.Proof));
    }

    public static string Line(string type, string? code = null) =>
        code is null ? new JsonObject { ["t"] = type }.ToJsonString() : new JsonObject { ["t"] = type, ["code"] = code }.ToJsonString();

    /// <summary>Reads one '\n'-terminated line byte by byte (never past it: the pipe starts right after). Null at end of stream.</summary>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxLine];
        var one = new byte[1];
        var length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(one, ct);
            if (read == 0) return length == 0 ? null : throw new IOException("Relay connection closed mid-line");
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buffer, 0, length);
            if (length == MaxLine) throw new InvalidDataException("Relay line too long");
            buffer[length++] = one[0];
        }
    }

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>The "t" (and "code") of a relay line; ("?", null) if it isn't one.</summary>
    public static (string Type, string? Code) ParseReply(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o) return ("?", null);
            return (o["t"]?.GetValue<string>() ?? "?", o["code"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return ("?", null); }
    }
}

/// <summary>Dials a relay and joins a room.</summary>
public static class RelayClient
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    /// <summary>The relay pings a waiting PC every 25 s; this long without a line means the relay is gone.</summary>
    public static readonly TimeSpan DefaultLineTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Connects and sends the hello. A phone gets the pipe at once (or <see cref="RelayRefusedException"/>
    /// "pc_offline"); a PC waits — possibly for hours — until a phone joins, then gets the pipe.
    /// <paramref name="waiting"/> is called once the relay has accepted a PC's hello.
    /// </summary>
    public static async Task<TcpClient> JoinAsync(RelayLink link, string role, string room, CancellationToken ct,
        Func<DateTimeOffset>? now = null, Action? waiting = null, TimeSpan? lineTimeout = null)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(link.Host, link.Port, connect.Token);
            }
            var stream = client.GetStream();
            await RelayProtocol.WriteLineAsync(stream, RelayProtocol.Hello(link.Key, role, room, (now ?? (() => DateTimeOffset.UtcNow))()), ct);
            while (true)
            {
                string? line;
                using (var silence = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    silence.CancelAfter(lineTimeout ?? DefaultLineTimeout);
                    try { line = await RelayProtocol.ReadLineAsync(stream, silence.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("The relay stopped answering"); }
                }
                if (line is null) throw new IOException("Relay closed the connection");
                var (type, code) = RelayProtocol.ParseReply(line);
                switch (type)
                {
                    case "paired": return client;
                    case "waiting": waiting?.Invoke(); break;
                    case "ping": break;
                    case "error": throw new RelayRefusedException(code is { Length: <= 32 } c && c.All(ch => char.IsAsciiLetterLower(ch) || ch == '_') ? c : "unknown");
                    default: throw new IOException("Unexpected reply from the relay");
                }
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
