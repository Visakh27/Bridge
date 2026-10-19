using Bridge.Core;
using Bridge.Network;
using Bridge.Protocol;
using Bridge.Security;
using Xunit;

namespace Bridge.Tests;

/// <summary>Phase E on the PC: browsing the phone's photos.</summary>
public sealed class PhotoBrowserTests : IDisposable
{
    private readonly DeviceIdentity _pc = DeviceIdentity.Generate();
    private readonly InMemoryPairedDeviceStore _store = new();
    private readonly TcpBridgeListener _listener = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly FileTransferService _files = new();
    private readonly string _downloads = Loopback.TempDir();
    private readonly BridgeHost _host;
    private readonly PairingService _pairing;

    public PhotoBrowserTests()
    {
        _pairing = new PairingService(_pc, _store, "PC");
        _host = new BridgeHost(_pc, _store, _pairing, _files, new ClipboardSyncService(new NoClipboard()), _downloads);
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

    private async Task<EncryptedChannel> Connect(DeviceIdentity phone)
    {
        var t = await TcpTransport.ConnectAsync("127.0.0.1", _listener.Port);
        return await EncryptedChannel.HandshakeAsInitiatorAsync(t, phone, _pc.PublicKeyBlob).WithTimeout();
    }

    /// <summary>A paired, connected phone; <paramref name="photos"/>: it advertises the feature.</summary>
    private async Task<(EncryptedChannel Ch, string[] PcFeatures)> PhoneAsync(bool photos = true)
    {
        var phone = DeviceIdentity.Generate();
        var qr = _pairing.CreateQrPayload(_listener.Port, "127.0.0.1");
        await using (var p = await Connect(phone))
        {
            await p.SendAsync(BridgeMessage.Create(MessageType.PairingRequest,
                new PairingRequestPayload(qr.PairingToken, "Pixel", Convert.ToBase64String(phone.PublicKeyBlob))));
            await p.ReceiveAsync().WithTimeout();
        }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(BridgeSession _) => started.TrySetResult();
        _host.SessionStarted += On;
        var ch = await Connect(phone);
        string[] f = photos ? [Features.Files, Features.FilesResume, Features.Photos] : [Features.Files];
        await ch.SendAsync(BridgeMessage.Create(MessageType.CapabilitiesAdvertise, new CapabilitiesPayload(f)));
        var reply = await ch.ReceiveAsync().WithTimeout();
        await started.Task.WithTimeout();
        _host.SessionStarted -= On;
        return (ch, reply.PayloadAs<CapabilitiesPayload>().Features);
    }

    private static async Task<BridgeMessage> Next(EncryptedChannel ch, string type)
    {
        while (true) { var m = await ch.ReceiveAsync().WithTimeout(); if (m.Type == type) return m; }
    }

    private static PhotoInfo Photo(string id, string name = "IMG.jpg", string mime = "image/jpeg") => new(id, name, 1_700_000_000_000, 4000, 3000, 2_000_000, mime);

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    [Fact]
    public async Task Lists_A_Page_And_Cleans_What_The_Phone_Sends()
    {
        var browser = new PhotoBrowser(_host, _files);
        var (ch, pcFeatures) = await PhoneAsync();
        await using (ch)
        {
            Assert.True(pcFeatures.Contains(Features.Photos));
            Assert.True(browser.PhoneSupportsPhotos);
            var listing = browser.ListAsync(before: "1700000000000:42", limit: 500);
            var req = (await Next(ch, MessageType.PhotosList)).PayloadAs<PhotosListPayload>();
            Assert.Equal(100, req.Limit); // clamped
            Assert.Equal("1700000000000:42", req.Before);
            await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosPage, new PhotosPagePayload(req.RequestId, PhotoStatus.Ok,
            [
                Photo("7", "..\\..\\evil\u202Egpj.exe"), Photo("../8"), Photo("9", "", "text/html"), Photo("10", "IMG_10.HEIC", "image/heic"),
            ]) { Next = "1600000000000:9" }));
            var page = await listing.WithTimeout();
            Assert.Equal(PhotoStatus.Ok, page.Status);
            Assert.Equal(["7", "9", "10"], page.Items.Select(i => i.Id));
            Assert.DoesNotContain("\\", page.Items[0].Name);
            Assert.DoesNotContain("\u202E", page.Items[0].Name);
            Assert.Equal("photo_9.jpg", page.Items[1].Name);
            Assert.Equal("image/*", page.Items[1].Mime);
            Assert.Equal("image/heic", page.Items[2].Mime);
            Assert.Equal("1600000000000:9", page.Next);

            // A malformed cursor from the phone ends paging rather than being echoed back.
            var second = browser.ListAsync(page.Next);
            var r2 = (await Next(ch, MessageType.PhotosList)).PayloadAs<PhotosListPayload>();
            await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosPage, new PhotosPagePayload(r2.RequestId, PhotoStatus.Ok, [Photo("1")]) { Next = "../../x" }));
            Assert.Null((await second.WithTimeout()).Next);
            Assert.Equal(PhotoStatus.Invalid, (await browser.ListAsync("not a cursor")).Status);
        }
    }

    [Fact]
    public async Task Phone_Refusals_Come_Through_With_Something_To_Say()
    {
        var browser = new PhotoBrowser(_host, _files);
        var (ch, _) = await PhoneAsync();
        await using (ch)
        {
            foreach (var status in new[] { PhotoStatus.Disabled, PhotoStatus.NoPermission, PhotoStatus.Busy })
            {
                var listing = browser.ListAsync();
                var req = (await Next(ch, MessageType.PhotosList)).PayloadAs<PhotosListPayload>();
                await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosPage, new PhotosPagePayload(req.RequestId, status, [Photo("1")])));
                var page = await listing.WithTimeout();
                Assert.Equal(status, page.Status);
                Assert.Empty(page.Items);
                Assert.NotNull(PhotoBrowser.Explain(status));
            }
            Assert.Contains("Let your PC browse photos", PhotoBrowser.Explain(PhotoStatus.Disabled)!);
            Assert.Null(PhotoBrowser.Explain(PhotoStatus.Ok));
        }
    }

    [Fact]
    public async Task No_Phone_Old_Phone_Silence_Or_A_Drop_Mean_Unavailable()
    {
        var browser = new PhotoBrowser(_host, _files, timeout: TimeSpan.FromMilliseconds(400));
        Assert.Equal(PhotoStatus.Unavailable, (await browser.ListAsync().WithTimeout()).Status);
        Assert.False(browser.PhoneSupportsPhotos);

        var (old, _) = await PhoneAsync(photos: false);
        await using (old)
        {
            Assert.False(browser.PhoneSupportsPhotos);
            Assert.Equal(PhotoStatus.Unavailable, (await browser.ThumbnailAsync("1").WithTimeout()).Status);
            await old.SendAsync(BridgeMessage.CreateEmpty(MessageType.PresencePing));
            Assert.Equal(MessageType.PresencePong, (await old.ReceiveAsync().WithTimeout()).Type); // nothing was sent to it
        }

        var (ch, _) = await PhoneAsync();
        Assert.Equal(PhotoStatus.Unavailable, (await browser.ListAsync().WithTimeout()).Status); // it never answers
        var slow = new PhotoBrowser(_host, _files, timeout: TimeSpan.FromSeconds(30));
        var waiting = slow.ListAsync();
        await Next(ch, MessageType.PhotosList);
        var t0 = DateTime.UtcNow;
        await ch.DisposeAsync();
        Assert.Equal(PhotoStatus.Unavailable, (await waiting.WithTimeout()).Status);
        Assert.True(DateTime.UtcNow - t0 < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Previews_Are_Checked_Cached_And_Asked_For_Once()
    {
        var browser = new PhotoBrowser(_host, _files);
        var (ch, _) = await PhoneAsync();
        await using (ch)
        {
            // Two at once: one request.
            var a = browser.ThumbnailAsync("5");
            var b = browser.ThumbnailAsync("5");
            var req = (await Next(ch, MessageType.PhotosThumb)).PayloadAs<PhotosThumbPayload>();
            await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosThumbnail, new PhotosThumbnailPayload(req.RequestId, "5", PhotoStatus.Ok)
                { Mime = "image/jpeg", Data = Convert.ToBase64String(Jpeg) }));
            Assert.Equal(Jpeg, (await a.WithTimeout()).Jpeg);
            Assert.Equal(Jpeg, (await b.WithTimeout()).Jpeg);
            // Cached: answered without asking.
            Assert.Equal(Jpeg, (await browser.ThumbnailAsync("5").WithTimeout(2)).Jpeg);

            async Task<PhotoThumb> Bad(string id, PhotosThumbnailPayload answer)
            {
                var t = browser.ThumbnailAsync(id);
                var r = (await Next(ch, MessageType.PhotosThumb)).PayloadAs<PhotosThumbPayload>();
                await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosThumbnail, answer with { RequestId = r.RequestId }));
                return await t.WithTimeout();
            }
            Assert.Equal(PhotoStatus.Error, (await Bad("6", new("", "6", "ok") { Mime = "image/jpeg", Data = Convert.ToBase64String("<svg>"u8.ToArray()) })).Status);
            Assert.Equal(PhotoStatus.Error, (await Bad("7", new("", "7", "ok") { Mime = "image/png", Data = Convert.ToBase64String(Jpeg) })).Status);
            Assert.Equal(PhotoStatus.Error, (await Bad("8", new("", "8", "ok") { Mime = "image/jpeg", Data = "!!not base64!!" })).Status);
            var huge = new byte[PhotoBrowser.MaxThumbBytes + 10]; huge[0] = 0xFF; huge[1] = 0xD8;
            Assert.Equal(PhotoStatus.Error, (await Bad("9", new("", "9", "ok") { Mime = "image/jpeg", Data = Convert.ToBase64String(huge) })).Status);
            Assert.Equal(PhotoStatus.Error, (await Bad("10", new("", "11", "ok") { Mime = "image/jpeg", Data = Convert.ToBase64String(Jpeg) })).Status); // wrong photo
            Assert.Equal(PhotoStatus.NotFound, (await Bad("12", new("", "12", "not_found"))).Status);
            // Failures aren't cached: asking again asks the phone again.
            var again = browser.ThumbnailAsync("12");
            await Next(ch, MessageType.PhotosThumb);
            Assert.False(again.IsCompleted);

            Assert.Equal(PhotoStatus.Invalid, (await browser.ThumbnailAsync("../1")).Status);
            browser.ClearCache();
            var afterClear = browser.ThumbnailAsync("5");
            await Next(ch, MessageType.PhotosThumb); // asked again
        }
    }

    [Fact]
    public async Task Stray_Answers_Are_Ignored()
    {
        var browser = new PhotoBrowser(_host, _files);
        var (ch, _) = await PhoneAsync();
        await using (ch)
        {
            var listing = browser.ListAsync();
            var req = (await Next(ch, MessageType.PhotosList)).PayloadAs<PhotosListPayload>();
            await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosPage, new PhotosPagePayload("someone-else", "ok", [Photo("1")])));
            await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosPage, new { requestId = 12345, status = "ok", items = Array.Empty<object>() }));
            await ch.SendAsync(BridgeMessage.CreateEmpty(MessageType.PhotosThumbnail));
            await Task.Delay(200);
            Assert.False(listing.IsCompleted);
            await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosPage, new PhotosPagePayload(req.RequestId, "ok", [Photo("2")])));
            Assert.Equal("2", (await listing.WithTimeout()).Items.Single().Id);
        }
    }

    private static async Task SendFile(EncryptedChannel ch, string transferId, string name, byte[] content, bool resumable, bool corrupt = false)
    {
        await ch.SendAsync(BridgeMessage.Create(MessageType.FileStart, new FileStartPayload(transferId, name, content.Length,
            FileHasher.Sha256Hex(corrupt ? [.. content, 1] : content), 1) { Resumable = resumable ? true : null }));
        if (resumable) await Next(ch, MessageType.FileResume);
        await ch.SendAsync(BridgeMessage.Create(MessageType.FileChunk, new FileChunkPayload(transferId, 0, Convert.ToBase64String(content))));
        await ch.SendAsync(BridgeMessage.Create(MessageType.FileEnd, new FileEndPayload(transferId)));
    }

    [Fact]
    public async Task Fetch_Waits_For_The_Announced_Transfer()
    {
        var browser = new PhotoBrowser(_host, _files);
        var (ch, _) = await PhoneAsync();
        await using (ch)
        {
            var content = "original pixels"u8.ToArray();
            var fetching = browser.FetchAsync("3");
            var req = (await Next(ch, MessageType.PhotosFetch)).PayloadAs<PhotosFetchPayload>();
            Assert.Equal("3", req.Id);
            // Another transfer finishing meanwhile isn't mistaken for this photo.
            await SendFile(ch, "other", "unrelated.txt", "x"u8.ToArray(), resumable: true);
            await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosFetched, new PhotosFetchedPayload(req.RequestId, "3", "ok") { TransferId = "t-3" }));
            await Task.Delay(100);
            Assert.False(fetching.IsCompleted);
            await SendFile(ch, "t-3", "IMG_3.jpg", content, resumable: true);
            var result = await fetching.WithTimeout();
            Assert.Equal(PhotoStatus.Ok, result.Status);
            Assert.Equal(content, File.ReadAllBytes(result.SavedPath!));
            Assert.Equal("IMG_3.jpg", Path.GetFileName(result.SavedPath));

            // The transfer can even finish before the answer is read.
            var quick = browser.FetchAsync("4");
            var r4 = (await Next(ch, MessageType.PhotosFetch)).PayloadAs<PhotosFetchPayload>();
            await SendFile(ch, "t-4", "IMG_4.jpg", content, resumable: false);
            await Task.Delay(200);
            await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosFetched, new PhotosFetchedPayload(r4.RequestId, "4", "ok") { TransferId = "t-4" }));
            Assert.Equal(PhotoStatus.Ok, (await quick.WithTimeout()).Status);

            var gone = browser.FetchAsync("5");
            var r5 = (await Next(ch, MessageType.PhotosFetch)).PayloadAs<PhotosFetchPayload>();
            await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosFetched, new PhotosFetchedPayload(r5.RequestId, "5", "not_found")));
            Assert.Equal(PhotoStatus.NotFound, (await gone.WithTimeout()).Status);

            var broken = browser.FetchAsync("6");
            var r6 = (await Next(ch, MessageType.PhotosFetch)).PayloadAs<PhotosFetchPayload>();
            await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosFetched, new PhotosFetchedPayload(r6.RequestId, "6", "ok") { TransferId = "t-6" }));
            await SendFile(ch, "t-6", "IMG_6.jpg", content, resumable: false, corrupt: true);
            Assert.Equal(PhotoStatus.Error, (await broken.WithTimeout()).Status);

            var noId = browser.FetchAsync("7");
            var r7 = (await Next(ch, MessageType.PhotosFetch)).PayloadAs<PhotosFetchPayload>();
            await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosFetched, new PhotosFetchedPayload(r7.RequestId, "7", "ok")));
            Assert.Equal(PhotoStatus.Error, (await noId.WithTimeout()).Status);
            Assert.Equal(PhotoStatus.Invalid, (await browser.FetchAsync("C:\\photo.jpg")).Status);
        }
    }

    [Fact]
    public async Task A_Fetch_Cut_Off_Mid_Transfer_Is_Interrupted_Not_Failed()
    {
        var browser = new PhotoBrowser(_host, _files);
        var (ch, _) = await PhoneAsync();
        var fetching = browser.FetchAsync("9");
        var req = (await Next(ch, MessageType.PhotosFetch)).PayloadAs<PhotosFetchPayload>();
        await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosFetched, new PhotosFetchedPayload(req.RequestId, "9", "ok") { TransferId = "t-9" }));
        var content = new byte[FileTransferService.ChunkSize * 3];
        await ch.SendAsync(BridgeMessage.Create(MessageType.FileStart, new FileStartPayload("t-9", "IMG_9.jpg", content.Length, FileHasher.Sha256Hex(content), 3) { Resumable = true }));
        await Next(ch, MessageType.FileResume);
        await ch.SendAsync(BridgeMessage.Create(MessageType.FileChunk, new FileChunkPayload("t-9", 0, Convert.ToBase64String(content[..FileTransferService.ChunkSize]))));
        await Task.Delay(100);
        await ch.DisposeAsync(); // Wi-Fi drops
        var result = await fetching.WithTimeout();
        Assert.Equal("interrupted", result.Status);
        Assert.Contains("reconnects", PhotoBrowser.Explain(result.Status)!);
    }

    [Fact]
    public async Task Changes_From_The_Phone_Are_Announced_And_Logged_Safely()
    {
        var browser = new PhotoBrowser(_host, _files);
        var log = new DiagnosticsLog(null);
        _ = new BridgeDiagnostics(log, _host, _files, photos: browser);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.Changed += () => changed.TrySetResult();
        var (ch, _) = await PhoneAsync();
        await using (ch)
        {
            await ch.SendAsync(BridgeMessage.CreateEmpty(MessageType.PhotosChanged));
            await changed.Task.WithTimeout();
            var listing = browser.ListAsync();
            var req = (await Next(ch, MessageType.PhotosList)).PayloadAs<PhotosListPayload>();
            await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosPage, new PhotosPagePayload(req.RequestId, "ok", [Photo("77", "Holiday_ZEBRA.jpg")])));
            await listing.WithTimeout();
            foreach (var (id, status) in new[] { ("77", "ok"), ("78", "not_found") })
            {
                var t = browser.ThumbnailAsync(id);
                var r = (await Next(ch, MessageType.PhotosThumb)).PayloadAs<PhotosThumbPayload>();
                await ch.SendAsync(BridgeMessage.Create(MessageType.PhotosThumbnail, new PhotosThumbnailPayload(r.RequestId, id, status)
                    { Mime = status == "ok" ? "image/jpeg" : null, Data = status == "ok" ? Convert.ToBase64String(Jpeg) : null }));
                await t.WithTimeout();
            }
        }
        var text = string.Join("\n", log.Lines());
        Assert.Contains("photos.request kind=list status=ok", text);
        Assert.Contains("photos.request kind=thumb status=not_found", text);
        Assert.DoesNotContain("kind=thumb status=ok", text); // previews load by the dozen: only failures are logged
        Assert.DoesNotContain("ZEBRA", text);
        Assert.DoesNotContain("77", text.Replace("photos.request", ""));
    }
}
