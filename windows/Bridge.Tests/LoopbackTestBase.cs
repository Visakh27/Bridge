using Bridge.Network;

namespace Bridge.Tests;

/// <summary>Real TCP loopback connections for exercising the handshake and sessions.</summary>
internal static class Loopback
{
    public static async Task<(TcpTransport Client, TcpTransport Server)> ConnectAsync()
    {
        using var listener = new TcpBridgeListener(0);
        var accept = listener.AcceptAsync();
        var client = await TcpTransport.ConnectAsync("127.0.0.1", listener.Port);
        return (client, await accept);
    }

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bridge-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static Task<T> WithTimeout<T>(this Task<T> task, int seconds = 10) => task.WaitAsync(TimeSpan.FromSeconds(seconds));

    public static Task WithTimeout(this Task task, int seconds = 10) => task.WaitAsync(TimeSpan.FromSeconds(seconds));
}
