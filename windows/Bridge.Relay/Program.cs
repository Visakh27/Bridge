using System.Net;
using System.Net.Sockets;
using Bridge.Network;

namespace Bridge.Relay;

/// <summary>
/// Runs a relay. Settings come from the environment:
///   BRIDGE_RELAY_PORT  TCP port (default 47830)
///   BRIDGE_RELAY_KEY   the relay key, base64url (32 bytes); if unset, a key is created once in
///                      BRIDGE_RELAY_DATA (default ./data)/relay.key and reused.
/// On start it prints the relay link to paste into Bridge on the PC (Settings › Remote access).
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("BRIDGE_RELAY_PORT"), out var p) && p is > 0 and <= 65535 ? p : RelayLink.DefaultPort;
        byte[]? key;
        var fromEnv = Environment.GetEnvironmentVariable("BRIDGE_RELAY_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            key = RelayLink.DecodeKey(fromEnv.Trim());
            if (key is null) { Console.Error.WriteLine("BRIDGE_RELAY_KEY must be 32 bytes in base64url (43 characters)."); return 2; }
        }
        else
        {
            var dataDir = Environment.GetEnvironmentVariable("BRIDGE_RELAY_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);
            var keyFile = Path.Combine(dataDir, "relay.key");
            key = File.Exists(keyFile) ? RelayLink.DecodeKey(File.ReadAllText(keyFile).Trim()) : null;
            if (key is null)
            {
                key = RelayLink.NewKey();
                File.WriteAllText(keyFile, RelayLink.EncodeKey(key));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Console.WriteLine($"Created a new relay key in {keyFile}");
            }
        }

        var host = args.FirstOrDefault() ?? "<this-server's-address>";
        Console.WriteLine("Bridge relay. In Bridge on your PC, open Settings › Remote access and paste:");
        Console.WriteLine($"  {RelayLink.Scheme}{host}:{port}#{RelayLink.EncodeKey(key)}");
        Console.WriteLine("Keep this link private: anyone with it can use this relay (but can't read anything passing through).");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
        await using var relay = new RelayServer(key);
        relay.Log += (ev, detail) => Console.WriteLine($"{DateTimeOffset.UtcNow:O} {ev} {detail}");
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        await relay.RunAsync(listener, cts.Token);
        return 0;
    }
}
