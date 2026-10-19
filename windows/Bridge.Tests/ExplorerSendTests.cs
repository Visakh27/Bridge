using System.Collections.Concurrent;
using Bridge.Core;
using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

/// <summary>Phase F: File Explorer's "Send to" / "Share" handing files to Bridge.</summary>
public sealed class ExplorerSendTests : IDisposable
{
    private readonly DeviceIdentity _pc = DeviceIdentity.Generate();
    private readonly InMemoryPairedDeviceStore _store = new();
    private readonly TcpBridgeListener _listener = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly BridgeHost _host;
    private readonly PairingService _pairing;
    private readonly string _src = Loopback.TempDir();

    public ExplorerSendTests()
    {
        _pairing = new PairingService(_pc, _store, "PC");
        _host = new BridgeHost(_pc, _store, _pairing, new FileTransferService(), new ClipboardSyncService(new NoClipboard()), Loopback.TempDir());
        _ = _host.RunAsync(_listener, _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _host.DisposeAsync().AsTask().Wait();
        _listener.Dispose();
    }

    private sealed class NoClipboard : IClipboardAccess
    {
        public event Action<string>? LocalClipboardChanged { add { } remove { } }
        public Task<string?> ReadTextAsync() => Task.FromResult<string?>(null);
        public Task WriteTextAsync(string text) => Task.CompletedTask;
    }

    // ------------------------------------------------------------------ command line

    [Fact]
    public void Command_Lines_Split_Like_Windows()
    {
        Assert.Equal(["a", "b"], LaunchArguments.Split("a  b"));
        Assert.Equal([@"C:\Program Files\Bridge\Bridge.Windows.exe", "--send", @"C:\My Files\a b.txt", @"D:\x"],
            LaunchArguments.Split(@"""C:\Program Files\Bridge\Bridge.Windows.exe"" --send ""C:\My Files\a b.txt"" D:\x"));
        Assert.Equal(["a\"b"], LaunchArguments.Split(@"""a\""b"""));      // \" is a literal quote
        Assert.Equal([@"a\\b"], LaunchArguments.Split(@"a\\b"));          // backslashes not before a quote stay
        Assert.Equal([@"C:\Dir\", "next"], LaunchArguments.Split(@"""C:\Dir\\"" next")); // \\" = one backslash, then the quote ends
        Assert.Equal(["", "x"], LaunchArguments.Split(@""""" x"));         // an empty argument
        Assert.Equal(["a\"b"], LaunchArguments.Split(@"""a""""b"""));      // "" inside quotes
        Assert.Empty(LaunchArguments.Split(null));
        Assert.Empty(LaunchArguments.Split("   "));
    }

    [Fact]
    public void Send_Arguments_Keep_Only_Real_Absolute_Paths_Once()
    {
        var file = Path.Combine(_src, "a.txt"); File.WriteAllText(file, "a");
        var folder = Path.Combine(_src, "Album"); Directory.CreateDirectory(folder);
        Assert.Null(LaunchArguments.ParseSend(["Bridge.Windows.exe", file]));
        var r = LaunchArguments.ParseSend(["Bridge.Windows.exe", "--SEND", file, folder + Path.DirectorySeparatorChar, file.ToUpperInvariant() == file ? file : file,
            "relative.txt", Path.Combine(_src, "missing.txt"), "--minimized", ""])!;
        Assert.Equal([file], r.Files);
        Assert.Equal([folder], r.Folders); // trailing separator normalised
        Assert.Equal(["relative.txt", Path.Combine(_src, "missing.txt")], r.Skipped);
        Assert.Equal(2, r.Count);

        var many = Enumerable.Range(0, LaunchArguments.MaxItems + 5).Select(i => Path.Combine(_src, $"f{i}")).ToList();
        var capped = LaunchArguments.ParseSend(["--send", .. many], fileExists: _ => true, folderExists: _ => false)!;
        Assert.Equal(LaunchArguments.MaxItems, capped.Files.Count);
        Assert.Equal(5, capped.Skipped.Count);

        var fromString = LaunchArguments.ParseSend($"\"C:\\Bridge.Windows.exe\" --send \"{file}\"");
        Assert.Equal([file], fromString!.Files);
        Assert.True(LaunchArguments.HasFlag(["x", "--Minimized"], LaunchArguments.MinimizedFlag));
    }

    // ------------------------------------------------------------------ the queue, with a phone that receives files

    /// <summary>A paired phone that takes whatever it's sent and acknowledges it.</summary>
    private sealed class ReceivingPhone : IAsyncDisposable
    {
        public readonly ConcurrentQueue<(string Name, string Content)> Received = new();
        private readonly EncryptedChannel _ch;
        private readonly Task _loop;
        private ReceivingPhone(EncryptedChannel ch) { _ch = ch; _loop = Task.Run(LoopAsync); }

        public static async Task<ReceivingPhone> ConnectAsync(ExplorerSendTests t, DeviceIdentity phone)
        {
            var transport = await TcpTransport.ConnectAsync("127.0.0.1", t._listener.Port);
            var ch = await EncryptedChannel.HandshakeAsInitiatorAsync(transport, phone, t._pc.PublicKeyBlob).WithTimeout();
            await ch.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload([Features.Files])));
            await ch.ReceiveAsync().WithTimeout();
            return new ReceivingPhone(ch);
        }

        private async Task LoopAsync()
        {
            var files = new Dictionary<string, (string Name, MemoryStream Data)>();
            try
            {
                while (true)
                {
                    var m = await _ch.ReceiveAsync();
                    switch (m.Type)
                    {
                        case MessageType.FileStart:
                            var s = m.PayloadAs<FileStartPayload>();
                            files[s.TransferId] = (s.FileName, new MemoryStream());
                            break;
                        case MessageType.FileChunk:
                            var c = m.PayloadAs<FileChunkPayload>();
                            files[c.TransferId].Data.Write(Convert.FromBase64String(c.Data));
                            break;
                        case MessageType.FileEnd:
                            var e = m.PayloadAs<FileEndPayload>();
                            var f = files[e.TransferId];
                            Received.Enqueue((f.Name, System.Text.Encoding.UTF8.GetString(f.Data.ToArray())));
                            await _ch.SendAsync(BridgeMessage.Create(MessageType.FileAck, new FileAckPayload(e.TransferId, FileAckStatus.Ok)));
                            break;
                        case MessageType.PresencePing:
                            await _ch.SendAsync(BridgeMessage.CreateEmpty(MessageType.PresencePong));
                            break;
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>A send to a phone without resume counts as sent once the last chunk is out; wait for the phone's side.</summary>
        public async Task WaitForAsync(int count)
        {
            for (var i = 0; i < 400 && Received.Count < count; i++) await Task.Delay(25);
        }

        public async ValueTask DisposeAsync() { await _ch.DisposeAsync(); try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
    }

    private async Task<DeviceIdentity> PairAsync()
    {
        var phone = DeviceIdentity.Generate();
        var qr = _pairing.CreateQrPayload(_listener.Port, "127.0.0.1");
        var t = await TcpTransport.ConnectAsync("127.0.0.1", _listener.Port);
        await using var ch = await EncryptedChannel.HandshakeAsInitiatorAsync(t, phone, _pc.PublicKeyBlob).WithTimeout();
        await ch.SendAsync(BridgeMessage.Create(MessageType.PairingRequest,
            new PairingRequestPayload(qr.PairingToken, "Pixel", Convert.ToBase64String(phone.PublicKeyBlob))));
        await ch.ReceiveAsync().WithTimeout();
        return phone;
    }

    private async Task WaitForSessionAsync()
    {
        for (var i = 0; i < 200 && _host.CurrentSession is not { IsRunning: true }; i++) await Task.Delay(25);
    }

    private SendRequest Files(params string[] names)
    {
        var paths = names.Select(n => { var p = Path.Combine(_src, n); File.WriteAllText(p, "content of " + n); return p; }).ToList();
        return new SendRequest(paths, [], []);
    }

    [Fact]
    public async Task Sends_At_Once_While_The_Phone_Is_Connected()
    {
        var phone = await PairAsync();
        var queue = new ExplorerSendQueue(_host);
        await using var p = await ReceivingPhone.ConnectAsync(this, phone);
        await WaitForSessionAsync();
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Started += (_, name) => started.TrySetResult(name);
        var summary = await queue.SubmitAsync(Files("a.txt", "b.txt")).WithTimeout();
        Assert.Equal(new SendSummary(2, 2, 0, 0, 0, false, "Pixel"), summary);
        Assert.Equal("Pixel", await started.Task.WithTimeout());
        await p.WaitForAsync(2);
        Assert.Equal(["a.txt", "b.txt"], p.Received.Select(r => r.Name));
        Assert.Equal("content of a.txt", p.Received.First().Content);
        Assert.Equal("Sent 2 files to Pixel.", ExplorerSendQueue.Describe(summary!));
    }

    [Fact]
    public async Task Waits_For_The_Phone_And_Sends_When_It_Reconnects()
    {
        var phone = await PairAsync();
        var queue = new ExplorerSendQueue(_host, startDelay: TimeSpan.FromMilliseconds(50));
        var queued = new List<int>();
        queue.Queued += (_, n) => { lock (queued) queued.Add(n); };
        var finished = new TaskCompletionSource<SendSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Finished += s => { if (s.WasQueued && s.Total == 2) finished.TrySetResult(s); };

        Assert.Null(await queue.SubmitAsync(Files("first.txt")));
        Assert.Null(await queue.SubmitAsync(Files("second.txt")));
        Assert.Equal([1, 2], queued);
        Assert.Equal(2, queue.PendingItems);

        // The second request is sent too, in order.
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = 0;
        queue.Finished += s => { if (s.WasQueued && Interlocked.Increment(ref done) == 2) both.TrySetResult(); };
        await using var p = await ReceivingPhone.ConnectAsync(this, phone);
        await both.Task.WithTimeout();
        await p.WaitForAsync(2);
        Assert.Equal(["first.txt", "second.txt"], p.Received.Select(r => r.Name));
        Assert.Equal(0, queue.PendingItems);
    }

    [Fact]
    public async Task Waiting_Sends_Expire_Or_Can_Be_Cancelled()
    {
        var phone = await PairAsync();
        var queue = new ExplorerSendQueue(_host, wait: TimeSpan.FromMilliseconds(300), startDelay: TimeSpan.FromMilliseconds(50));
        var expired = new TaskCompletionSource<SendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Expired += r => expired.TrySetResult(r);
        await queue.SubmitAsync(Files("late.txt"));
        Assert.Single((await expired.Task.WithTimeout()).Files);
        Assert.Equal(0, queue.PendingItems);

        var longer = new ExplorerSendQueue(_host, wait: TimeSpan.FromMinutes(5), startDelay: TimeSpan.FromMilliseconds(50));
        var expiredToo = false;
        longer.Expired += _ => expiredToo = true;
        await longer.SubmitAsync(Files("x.txt", "y.txt"));
        Assert.Equal(2, longer.CancelPending());
        Assert.Equal(0, longer.CancelPending());

        await using var p = await ReceivingPhone.ConnectAsync(this, phone);
        await WaitForSessionAsync();
        await Task.Delay(500);
        Assert.Empty(p.Received); // neither the expired nor the cancelled ones were sent
        Assert.False(expiredToo);
    }

    [Fact]
    public async Task Files_Gone_While_Waiting_Are_Reported_And_Folders_Are_Sent()
    {
        var phone = await PairAsync();
        var queue = new ExplorerSendQueue(_host, startDelay: TimeSpan.FromMilliseconds(50));
        var album = Path.Combine(_src, "Album");
        Directory.CreateDirectory(Path.Combine(album, "day1"));
        File.WriteAllText(Path.Combine(album, "day1", "p1.jpg"), "p1");
        File.WriteAllText(Path.Combine(album, "p2.jpg"), "p2");
        var files = Files("kept.txt", "deleted.txt");
        var request = files with { Folders = [album] };
        var finished = new TaskCompletionSource<SendSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Finished += s => finished.TrySetResult(s);
        await queue.SubmitAsync(request);
        File.Delete(files.Files[1]);
        await using var p = await ReceivingPhone.ConnectAsync(this, phone);
        var summary = await finished.Task.WithTimeout();
        await p.WaitForAsync(3);
        Assert.Equal(4, summary.Total);
        Assert.Equal(3, summary.Sent);
        Assert.Equal(1, summary.Failed);
        Assert.True(summary.WasQueued);
        Assert.Equal(["kept.txt", "p1.jpg", "p2.jpg"], p.Received.Select(r => r.Name).OrderBy(n => n).ToArray());
        Assert.Equal("Sent 3 files; 1 file didn't transfer (see Recent transfers to retry).", ExplorerSendQueue.Describe(summary));
    }

    [Fact]
    public async Task A_Phone_That_Drops_Again_Before_Sending_Gets_It_Next_Time()
    {
        var phone = await PairAsync();
        var queue = new ExplorerSendQueue(_host, startDelay: TimeSpan.FromMilliseconds(600));
        await queue.SubmitAsync(Files("patient.txt"));
        var p1 = await ReceivingPhone.ConnectAsync(this, phone);
        await WaitForSessionAsync();
        await p1.DisposeAsync(); // gone before the queue got going
        for (var i = 0; i < 100 && _host.CurrentSession is not null; i++) await Task.Delay(20);
        await Task.Delay(900);
        Assert.Empty(p1.Received);
        Assert.Equal(1, queue.PendingItems); // still waiting, not lost
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Finished += _ => done.TrySetResult();
        await using var p2 = await ReceivingPhone.ConnectAsync(this, phone);
        await done.Task.WithTimeout();
        await p2.WaitForAsync(1);
        Assert.Equal(["patient.txt"], p2.Received.Select(r => r.Name));
    }

    [Fact]
    public void Summaries_Read_Naturally()
    {
        Assert.Equal("Nothing to send.", ExplorerSendQueue.Describe(new(0, 0, 0, 0, 0, false, null)));
        Assert.Equal("Sent 1 file to your phone.", ExplorerSendQueue.Describe(new(1, 1, 0, 0, 0, false, null)));
        Assert.Equal("Sent 2 files; 1 file will finish when your phone reconnects.", ExplorerSendQueue.Describe(new(3, 2, 0, 1, 0, true, "P")));
        Assert.Equal("1 file cancelled.", ExplorerSendQueue.Describe(new(1, 0, 0, 0, 1, false, "P")));
    }

    [Fact]
    public async Task An_Empty_Request_Sends_Nothing()
    {
        var queue = new ExplorerSendQueue(_host);
        var none = await queue.SubmitAsync(new SendRequest([], [], ["missing"]));
        Assert.Equal(0, none!.Total);
        Assert.Equal(0, queue.PendingItems);
    }

    [Fact]
    public void Cancel_Send_Toast_And_New_Settings()
    {
        var c = new NotificationToastCommand(NotificationToastCommand.CancelSend);
        Assert.Equal(c, NotificationToastCommand.Parse(new Dictionary<string, string>(c.ToArguments())));
        var s = new BridgeSettings();
        Assert.True(s.SendToMenu);
        Assert.False(s.QuickAccessPin);
        var path = Path.Combine(Loopback.TempDir(), "settings.json");
        new SettingsStore(path).Update(x => x with { SendToMenu = false, QuickAccessPin = true });
        var back = new SettingsStore(path).Current;
        Assert.False(back.SendToMenu);
        Assert.True(back.QuickAccessPin);
    }
}
