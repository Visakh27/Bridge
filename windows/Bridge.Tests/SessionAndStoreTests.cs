using Bridge.Core;
using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

public class SessionAndStoreTests
{
    [Fact]
    public async Task Watchdog_Ends_Session_When_Peer_Goes_Silent()
    {
        // Half-open TCP (phone left Wi-Fi without a FIN): no error ever arrives,
        // so only the presence watchdog can notice.
        var phone = DeviceIdentity.Generate();
        var pc = DeviceIdentity.Generate();
        var (client, server) = await Loopback.ConnectAsync();
        var respTask = EncryptedChannel.HandshakeAsResponderAsync(server, pc);
        await using var silentPhone = await EncryptedChannel.HandshakeAsInitiatorAsync(client, phone, pc.PublicKeyBlob);
        var channel = await respTask;

        var session = new BridgeSession(channel, new ConnectedDevice { DeviceId = phone.DeviceId, DeviceName = "p" },
            new FileTransferService(), new ClipboardSyncService(new NullClipboard()), Loopback.TempDir(),
            pingInterval: TimeSpan.FromMilliseconds(100), peerTimeout: TimeSpan.FromMilliseconds(600));
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Ended += _ => ended.TrySetResult();
        session.Start();

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.Device.IsConnected);
    }

    [Fact]
    public async Task JsonPairedDeviceStore_Persists_Keys_And_Revokes()
    {
        var path = Path.Combine(Loopback.TempDir(), "sub", "paired.json");
        var key = DeviceIdentity.Generate().PublicKeyBlob;
        var store = new JsonPairedDeviceStore(path);
        await store.SaveAsync(new PairedDevice("a", "Pixel", key));
        await store.SaveAsync(new PairedDevice("b", "Galaxy", [1, 2]));
        await store.SaveAsync(new PairedDevice("a", "Pixel 8", key)); // re-pair replaces

        var reloaded = await new JsonPairedDeviceStore(path).ListAsync();
        Assert.Equal(2, reloaded.Count);
        var a = reloaded.Single(d => d.DeviceId == "a");
        Assert.Equal("Pixel 8", a.DeviceName);
        Assert.Equal(key, a.LongTermPublicKey);

        await store.RevokeAsync("a");
        Assert.Equal("b", (await store.ListAsync()).Single().DeviceId);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task JsonPairedDeviceStore_Treats_Corrupt_File_As_Empty()
    {
        var path = Path.Combine(Loopback.TempDir(), "paired.json");
        await File.WriteAllTextAsync(path, "{ not json");
        var store = new JsonPairedDeviceStore(path);
        Assert.Empty(await store.ListAsync());
        await store.SaveAsync(new PairedDevice("x", "X", [1]));
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task Clipboard_Retries_Text_That_Failed_To_Send()
    {
        var clip = new FakeClipboard();
        var sync = new ClipboardSyncService(clip) { Enabled = true, PeerSupportsClipboard = true };
        var failures = 0;
        sync.SendFailed += _ => failures++;

        // A channel whose transport is already closed: every send fails.
        var (client, server) = await Loopback.ConnectAsync();
        var phone = DeviceIdentity.Generate();
        var pc = DeviceIdentity.Generate();
        var respTask = EncryptedChannel.HandshakeAsResponderAsync(server, pc);
        var init = await EncryptedChannel.HandshakeAsInitiatorAsync(client, phone, pc.PublicKeyBlob);
        var resp = await respTask;
        await resp.DisposeAsync();
        await init.DisposeAsync();
        sync.Channel = resp;

        clip.Copy("same text");
        await Task.Delay(100);
        clip.Copy("same text");
        await Task.Delay(100);
        Assert.Equal(2, failures); // second copy was retried, not swallowed as a duplicate
    }

    private sealed class NullClipboard : IClipboardAccess
    {
        public event Action<string>? LocalClipboardChanged { add { } remove { } }
        public Task<string?> ReadTextAsync() => Task.FromResult<string?>(null);
        public Task WriteTextAsync(string text) => Task.CompletedTask;
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public event Action<string>? LocalClipboardChanged;
        public Task<string?> ReadTextAsync() => Task.FromResult<string?>(null);
        public Task WriteTextAsync(string text) => Task.CompletedTask;
        public void Copy(string text) => LocalClipboardChanged?.Invoke(text);
    }
}
