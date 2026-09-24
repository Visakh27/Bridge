namespace Bridge.Core;

/// <summary>
/// Exponential-backoff reconnect loop shared by the Windows dashboard when a
/// BridgeSession drops unexpectedly (temporary Wi-Fi loss, phone screen
/// sleep, etc.) rather than an explicit unpair.
/// </summary>
public sealed class ReconnectManager
{
    private static readonly TimeSpan[] BackoffSteps =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    ];

    public async Task RunAsync(Func<CancellationToken, Task<bool>> tryReconnect, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var delay = BackoffSteps[Math.Min(attempt, BackoffSteps.Length - 1)];
            await Task.Delay(delay, ct);

            bool success;
            try { success = await tryReconnect(ct); }
            catch { success = false; }

            if (success) return;
            attempt++;
        }
    }
}
