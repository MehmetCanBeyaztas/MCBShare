using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MCBShare.Core;
using Microsoft.Extensions.Time.Testing;

namespace MCBShare.Core.Tests;

internal sealed class FakeNetwork(string? name) : INetworkIdentity
{
    public string? CurrentNetworkName { get; private set; } = name;
    public event Action? Changed;

    public void Switch(string? name)
    {
        CurrentNetworkName = name;
        Changed?.Invoke();
    }
}

public sealed class FileNamesTests
{
    [Theory]
    [InlineData("foto.jpg", "foto.jpg")]
    [InlineData("../../Windows/evil.exe", "evil.exe")]
    [InlineData(@"..\..\evil.exe", "evil.exe")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("a:b?.txt", "a_b_.txt")]
    [InlineData("nokta...", "nokta")]
    [InlineData("", "dosya")]
    [InlineData("..", "dosya")]
    [InlineData(".parts", "dosya")]
    public void Sanitize_makes_names_safe(string input, string expected) =>
        Assert.Equal(expected, FileNames.Sanitize(input));

    [Fact]
    public void Sanitize_limits_length_and_keeps_extension()
    {
        var result = FileNames.Sanitize(new string('a', 500) + ".jpg");
        Assert.Equal(200, result.Length);
        Assert.EndsWith(".jpg", result);
    }

    [Fact]
    public void Unique_numbers_conflicts()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        Assert.Equal(Path.Combine(dir, "foto.jpg"), FileNames.Unique(dir, "foto.jpg"));
        File.WriteAllText(Path.Combine(dir, "foto.jpg"), "");
        Assert.Equal(Path.Combine(dir, "foto (1).jpg"), FileNames.Unique(dir, "foto.jpg"));
        File.WriteAllText(Path.Combine(dir, "foto (1).jpg"), "");
        Assert.Equal(Path.Combine(dir, "foto (2).jpg"), FileNames.Unique(dir, "foto.jpg"));
        Directory.Delete(dir, true);
    }
}

public sealed class NetworkAddressTests
{
    [Theory]
    [InlineData("192.168.1.25", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::ffff:192.168.1.2", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void IsLocal_accepts_only_private_addresses(string ip, bool expected) =>
        Assert.Equal(expected, NetworkAddresses.IsLocal(IPAddress.Parse(ip)));
}

public sealed class ShareLifetimeTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly ShareService _shares;
    private readonly string _file;

    public ShareLifetimeTests()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        _file = Path.Combine(dir, "a.txt");
        File.WriteAllText(_file, "merhaba");
        _shares = new ShareService(_time, new FileLog(Path.Combine(dir, "logs"), _time));
    }

    [Fact]
    public void Waiting_code_dies_after_15_minutes()
    {
        var share = _shares.Create([_file]);
        _time.Advance(TimeSpan.FromMinutes(14));
        Assert.NotNull(_shares.Get(share.Code));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(_shares.Get(share.Code));
    }

    [Fact]
    public void Started_download_shortens_life_to_5_minutes()
    {
        var share = _shares.Create([_file]);
        _shares.BeginDownload(share.Code, 0);
        _time.Advance(TimeSpan.FromMinutes(4));
        Assert.NotNull(_shares.BeginDownload(share.Code, 0));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(_shares.Get(share.Code));
    }

    [Fact]
    public void Second_download_does_not_extend_life()
    {
        var share = _shares.Create([_file]);
        _shares.BeginDownload(share.Code, 0);
        _time.Advance(TimeSpan.FromMinutes(4));
        _shares.BeginDownload(share.Code, 0);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(_shares.Get(share.Code));
    }

    [Fact]
    public void Cancel_kills_code_immediately()
    {
        var share = _shares.Create([_file]);
        ShareState? last = null;
        _shares.ShareChanged += s => last = s.State;
        _shares.Cancel(share.Code);
        Assert.Null(_shares.Get(share.Code));
        Assert.Equal(ShareState.Cancelled, last);
    }

    [Fact]
    public void Reaching_full_size_marks_downloaded()
    {
        var share = _shares.Create([_file]);
        _shares.BeginDownload(share.Code, 0);
        _shares.ReportProgress(share.Code, 0, share.TotalBytes);
        Assert.Equal(ShareState.Downloaded, _shares.Get(share.Code)!.State);
    }
}

public sealed class ServerTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("mcbshare-test").FullName;
    private readonly FakeNetwork _network = new("Ev");
    private readonly HttpClient _http = new();
    private long _freeSpace = long.MaxValue;
    private MCBShareCore _core = null!;
    private string _key = "";

    private string Inbox => Path.Combine(_root, "inbox");

    public async Task InitializeAsync()
    {
        _core = new MCBShareCore(new CoreOptions
        {
            DataFolder = Path.Combine(_root, "data"),
            DefaultReceiveFolder = Inbox,
            Network = _network,
            BindAddress = IPAddress.Loopback,
            FreeSpace = _ => _freeSpace,
        });
        _core.Settings.Port = FreePort();
        _http.BaseAddress = new Uri($"http://127.0.0.1:{_core.Settings.Port}");
        await _core.TrustCurrentNetworkAsync();
        Assert.Equal(ServerState.Running, _core.State);

        var (_, key) = _core.Devices.Register("Test iPhone");
        _key = key;
        _http.DefaultRequestHeaders.Add("X-MCB-Key", key);
    }

    public async Task DisposeAsync()
    {
        await _core.DisposeAsync();
        _http.Dispose();
        Directory.Delete(_root, true);
    }

    [Fact]
    public async Task Upload_without_key_is_rejected()
    {
        using var anon = new HttpClient { BaseAddress = _http.BaseAddress };
        var r = await anon.PostAsync("/upload?name=a.txt", new StringContent("x"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.False(File.Exists(Path.Combine(Inbox, "a.txt")));
    }

    [Fact]
    public async Task Removed_device_loses_access()
    {
        _core.Devices.Remove(_core.Devices.Devices[0].Id);
        var r = await _http.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Pairing_requires_correct_token_and_returns_working_key()
    {
        using var anon = new HttpClient { BaseAddress = _http.BaseAddress };
        var bad = await anon.PostAsJsonAsync("/api/register", new { token = "yanlis", name = "X" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var ok = await anon.PostAsJsonAsync("/api/register", new { token = _core.Settings.PairingToken, name = "Android" });
        ok.EnsureSuccessStatusCode();
        var key = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString();

        anon.DefaultRequestHeaders.Add("X-MCB-Key", key);
        var me = await anon.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal("Android", me.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Repeated_failures_are_throttled()
    {
        using var anon = new HttpClient { BaseAddress = _http.BaseAddress };
        for (var i = 0; i < 20; i++)
            await anon.PostAsJsonAsync("/api/register", new { token = "yanlis", name = "X" });
        var r = await anon.PostAsJsonAsync("/api/register", new { token = _core.Settings.PairingToken, name = "X" });
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
    }

    [Fact]
    public async Task Simple_upload_saves_file_and_history()
    {
        var r = await _http.PostAsync("/upload?name=foto.jpg", new StringContent("icerik"));
        r.EnsureSuccessStatusCode();
        Assert.Equal("icerik", File.ReadAllText(Path.Combine(Inbox, "foto.jpg")));
        Assert.Empty(Directory.GetFiles(Path.Combine(Inbox, ".parts")));

        var item = Assert.Single(_core.History.Recent());
        Assert.Equal("foto.jpg", item.Name);
        Assert.Equal("Test iPhone", item.DeviceName);
    }

    [Fact]
    public async Task Filename_can_come_from_header()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/upload") { Content = new StringContent("x") };
        request.Headers.Add("X-MCB-Filename", Uri.EscapeDataString("çiçek.txt"));
        (await _http.SendAsync(request)).EnsureSuccessStatusCode();
        Assert.True(File.Exists(Path.Combine(Inbox, "çiçek.txt")));
    }

    [Fact]
    public async Task Same_name_different_content_is_numbered()
    {
        await _http.PostAsync("/upload?name=foto.jpg", new StringContent("bir"));
        await _http.PostAsync("/upload?name=foto.jpg", new StringContent("iki"));
        Assert.Equal("iki", File.ReadAllText(Path.Combine(Inbox, "foto (1).jpg")));
    }

    [Fact]
    public async Task Same_content_is_not_saved_twice()
    {
        await _http.PostAsync("/upload?name=a.jpg", new StringContent("ayni"));
        var r = await _http.PostAsync("/upload?name=b.jpg", new StringContent("ayni"));
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("duplicate").GetBoolean());
        Assert.Equal("a.jpg", body.GetProperty("name").GetString());
        Assert.False(File.Exists(Path.Combine(Inbox, "b.jpg")));
    }

    [Fact]
    public async Task Upload_resumes_from_last_offset()
    {
        var data = Encoding.UTF8.GetBytes("0123456789");
        var query = $"/upload?name=video.mp4&id=abcdefgh&size={data.Length}";

        var first = await _http.PostAsync(query + "&offset=0", new ByteArrayContent(data[..4]));
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(firstBody.GetProperty("complete").GetBoolean());
        Assert.False(File.Exists(Path.Combine(Inbox, "video.mp4")));

        var status = await _http.GetFromJsonAsync<JsonElement>("/upload/status?id=abcdefgh");
        Assert.Equal(4, status.GetProperty("offset").GetInt64());

        var wrong = await _http.PostAsync(query + "&offset=2", new ByteArrayContent(data[2..]));
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal(4, (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("offset").GetInt64());

        var last = await _http.PostAsync(query + "&offset=4", new ByteArrayContent(data[4..]));
        Assert.True((await last.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("complete").GetBoolean());
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(Inbox, "video.mp4")));
    }

    [Fact]
    public async Task Interrupted_stream_keeps_received_bytes_for_resume()
    {
        var port = _core.Settings.Port;
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            var head = $"POST /upload?name=v.bin&id=kesintili1&size=100&offset=0 HTTP/1.1\r\nHost: x\r\nX-MCB-Key: {_key}\r\nContent-Length: 100\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(new byte[30]);
            await stream.FlushAsync();
            await WaitUntil(async () => (await Offset("kesintili1")) == 30);
        }

        // Bağlantı kapandıktan sonra yazma kilidi bırakılmış olmalı.
        await WaitUntil(async () =>
        {
            var r = await _http.PostAsync("/upload?name=v.bin&id=kesintili1&size=100&offset=30", new ByteArrayContent(new byte[70]));
            return r.IsSuccessStatusCode;
        });
        Assert.Equal(100, new FileInfo(Path.Combine(Inbox, "v.bin")).Length);
    }

    [Fact]
    public async Task Interrupted_simple_upload_leaves_no_files()
    {
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, _core.Settings.Port);
            var stream = tcp.GetStream();
            var head = $"POST /upload?name=yarim.bin HTTP/1.1\r\nHost: x\r\nX-MCB-Key: {_key}\r\nContent-Length: 100\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(new byte[30]);
            await stream.FlushAsync();
            await WaitUntil(() => Task.FromResult(Directory.Exists(Path.Combine(Inbox, ".parts"))
                && Directory.GetFiles(Path.Combine(Inbox, ".parts")).Length == 1));
        }

        await WaitUntil(() => Task.FromResult(Directory.GetFiles(Path.Combine(Inbox, ".parts")).Length == 0));
        Assert.False(File.Exists(Path.Combine(Inbox, "yarim.bin")));
    }

    [Fact]
    public async Task No_disk_space_is_rejected_before_transfer()
    {
        _freeSpace = 5;
        var r = await _http.PostAsync("/upload?name=buyuk.bin", new ByteArrayContent(new byte[100]));
        Assert.Equal((HttpStatusCode)507, r.StatusCode);
        Assert.Equal("disk_full", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.False(File.Exists(Path.Combine(Inbox, "buyuk.bin")));
    }

    [Fact]
    public async Task Path_traversal_stays_in_receive_folder()
    {
        await _http.PostAsync("/upload?name=" + Uri.EscapeDataString("../../kacis.txt"), new StringContent("x"));
        Assert.True(File.Exists(Path.Combine(Inbox, "kacis.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "kacis.txt")));
    }

    [Fact]
    public async Task Modified_date_is_applied_to_file()
    {
        var date = new DateTimeOffset(2021, 5, 4, 12, 0, 0, TimeSpan.Zero);
        await _http.PostAsync($"/upload?name=eski.txt&modified={date.ToUnixTimeMilliseconds()}", new StringContent("x"));
        Assert.Equal(date.UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(Inbox, "eski.txt")));
    }

    [Fact]
    public async Task Organize_by_date_uses_year_and_month_folders()
    {
        _core.Settings.OrganizeByDate = true;
        var date = new DateTimeOffset(2021, 5, 15, 12, 0, 0, TimeSpan.Zero);
        await _http.PostAsync($"/upload?name=d.txt&modified={date.ToUnixTimeMilliseconds()}", new StringContent("x"));
        Assert.True(File.Exists(Path.Combine(Inbox, "2021", "05", "d.txt")));
    }

    [Fact]
    public async Task Transfer_events_report_sender_and_completion()
    {
        var updates = new List<TransferUpdate>();
        _core.Uploads.TransferChanged += updates.Add;
        await _http.PostAsync("/upload?name=e.txt&batchIndex=3&batchTotal=10", new StringContent("x"));

        var last = updates[^1];
        Assert.Equal(TransferState.Completed, last.State);
        Assert.Equal("Test iPhone", last.DeviceName);
        Assert.Equal(3, last.BatchIndex);
        Assert.Equal(10, last.BatchTotal);
        Assert.Equal(Path.Combine(Inbox, "e.txt"), last.FinalPath);
    }

    [Fact]
    public async Task Share_downloads_single_file_with_range_and_dies_on_cancel()
    {
        var file = Path.Combine(_root, "paylas.txt");
        File.WriteAllText(file, "0123456789");
        var share = _core.Shares.Create([file]);

        using var anon = new HttpClient { BaseAddress = _http.BaseAddress };
        var full = await anon.GetAsync($"/d/{share.Code}");
        Assert.Equal("0123456789", await full.Content.ReadAsStringAsync());
        Assert.Equal("paylas.txt", full.Content.Headers.ContentDisposition?.FileNameStar ?? full.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal(ShareState.Downloaded, _core.Shares.Get(share.Code)!.State);

        var range = new HttpRequestMessage(HttpMethod.Get, $"/d/{share.Code}");
        range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(5, null);
        var partial = await anon.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("56789", await partial.Content.ReadAsStringAsync());

        _core.Shares.Cancel(share.Code);
        Assert.Equal(HttpStatusCode.Gone, (await anon.GetAsync($"/d/{share.Code}")).StatusCode);
    }

    [Fact]
    public async Task Multiple_files_get_escaped_list_page()
    {
        var a = Path.Combine(_root, "a&b.txt");
        var b = Path.Combine(_root, "c.txt");
        File.WriteAllText(a, "A");
        File.WriteAllText(b, "B");
        var share = _core.Shares.Create([a, b]);

        using var anon = new HttpClient { BaseAddress = _http.BaseAddress };
        var page = await anon.GetStringAsync($"/d/{share.Code}");
        Assert.Contains($"/d/{share.Code}/0", page);
        Assert.Contains($"/d/{share.Code}/1", page);
        Assert.DoesNotContain("a&b.txt", page);
        Assert.Equal("B", await anon.GetStringAsync($"/d/{share.Code}/1"));
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/d/{share.Code}/9")).StatusCode);
    }

    [Fact]
    public async Task Clipboard_works_both_ways()
    {
        string? received = null;
        _core.Clipboard.TextReceived += (_, text) => received = text;
        (await _http.PostAsJsonAsync("/api/clipboard", new { text = "https://ornek.com" })).EnsureSuccessStatusCode();
        Assert.Equal("https://ornek.com", received);

        _core.Clipboard.SendToPhone("pc metni");
        var body = await _http.GetFromJsonAsync<JsonElement>("/api/clipboard");
        Assert.Equal("pc metni", body.GetProperty("text").GetString());
    }

    private static byte[] JpegWithBrokenXmp()
    {
        // Gerçek dosyada görülen durum: XMP bölümü var ama kodlaması tanınmıyor.
        var preamble = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        byte[] broken = [0x00, 0x00, 0xFE, 0xFF, 0x00, 0x3C, 0x00, 0x78, 0xD8, 0x00, 0x41, 0x42];
        var length = preamble.Length + broken.Length + 2;
        byte[] header = [0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length];
        return [.. header, .. preamble, .. broken, 0xFF, 0xD9];
    }

    [Fact]
    public void Broken_xmp_sample_really_breaks_the_metadata_reader()
    {
        var path = Path.Combine(_root, "bozuk.jpg");
        File.WriteAllBytes(path, JpegWithBrokenXmp());
        Assert.ThrowsAny<Exception>(() => MetadataExtractor.ImageMetadataReader.ReadMetadata(path));
    }

    [Fact]
    public async Task Broken_metadata_does_not_reject_the_file()
    {
        var data = JpegWithBrokenXmp();
        var date = new DateTimeOffset(2021, 5, 4, 12, 0, 0, TimeSpan.Zero);
        var r = await _http.PostAsync(
            $"/upload?name=bozuk.jpg&id=bozukxmp1&size={data.Length}&modified={date.ToUnixTimeMilliseconds()}",
            new ByteArrayContent(data));
        r.EnsureSuccessStatusCode();
        var path = Path.Combine(Inbox, "bozuk.jpg");
        Assert.Equal(data, File.ReadAllBytes(path));
        Assert.Equal(date.UtcDateTime, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Fully_received_part_finishes_with_an_empty_request()
    {
        // Önceki denemede son işlem başarısız olduysa bütün baytlar .part içinde bekler.
        var device = _core.Devices.Authenticate(_key)!;
        var parts = Path.Combine(Inbox, ".parts");
        Directory.CreateDirectory(parts);
        var part = Path.Combine(parts, Secrets.Hash(device.Id + ":bekleyen1")[..32] + ".part");
        File.WriteAllText(part, "0123456789");

        Assert.Equal(10, await Offset("bekleyen1"));
        var r = await _http.PostAsync("/upload?name=kurtar.txt&id=bekleyen1&size=10&offset=10", new ByteArrayContent([]));
        Assert.True((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("complete").GetBoolean());
        Assert.Equal("0123456789", File.ReadAllText(Path.Combine(Inbox, "kurtar.txt")));
        Assert.False(File.Exists(part));
    }

    [Fact]
    public async Task Client_log_requires_key_and_strips_line_breaks()
    {
        using var anon = new HttpClient { BaseAddress = _http.BaseAddress };
        var rejected = await anon.PostAsJsonAsync("/api/clientlog", new { message = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        (await _http.PostAsJsonAsync("/api/clientlog", new { message = "a\nFAKE ERROR" })).EnsureSuccessStatusCode();
        var log = File.ReadAllText(Directory.GetFiles(_core.Log.Folder).Single());
        Assert.Contains("Phone [Test iPhone]: aFAKE ERROR", log);
    }

    [Fact]
    public async Task Rejected_requests_are_logged_with_their_reason()
    {
        await _http.PostAsync("/upload?name=a.bin&id=abcdefgh&size=10&offset=5", new ByteArrayContent(new byte[5]));
        var log = File.ReadAllText(Directory.GetFiles(_core.Log.Folder).Single());
        Assert.Contains("Rejected POST /upload: 409 offset_mismatch", log);
    }

    [Fact]
    public async Task Sent_text_reports_first_delivery_per_device_only()
    {
        var delivered = new List<string>();
        _core.Clipboard.TextDelivered += delivered.Add;

        await _http.GetFromJsonAsync<JsonElement>("/api/clipboard");
        Assert.Empty(delivered);

        _core.Clipboard.SendToPhone("bir");
        await _http.GetFromJsonAsync<JsonElement>("/api/clipboard");
        await _http.GetFromJsonAsync<JsonElement>("/api/clipboard");
        Assert.Equal(["Test iPhone"], delivered);

        _core.Clipboard.SendToPhone("iki");
        await _http.GetFromJsonAsync<JsonElement>("/api/clipboard");
        Assert.Equal(2, delivered.Count);
    }

    [Fact]
    public void Sent_text_expires_after_15_minutes()
    {
        var time = new FakeTimeProvider();
        var dir = Path.Combine(_root, "clip-logs");
        var clipboard = new ClipboardExchange(time, new FileLog(dir, time));
        clipboard.SendToPhone("gizli");

        time.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal("gizli", clipboard.Latest().Text);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("", clipboard.Latest().Text);
        Assert.Null(clipboard.Latest().At);
    }

    [Fact]
    public async Task Server_follows_trusted_network()
    {
        _network.Switch("Kafe");
        await WaitUntil(() => Task.FromResult(_core.State == ServerState.UntrustedNetwork));
        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync("/api/me"));

        _network.Switch("Ev");
        await WaitUntil(() => Task.FromResult(_core.State == ServerState.Running));
        (await _http.GetAsync("/api/me")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Web_page_is_served_without_key()
    {
        using var anon = new HttpClient { BaseAddress = _http.BaseAddress };
        Assert.Contains("MCBShare", await anon.GetStringAsync("/"));
    }

    [Fact]
    public async Task Settings_and_devices_survive_restart()
    {
        var token = _core.Settings.PairingToken;
        await _core.DisposeAsync();
        await using var again = new MCBShareCore(new CoreOptions
        {
            DataFolder = Path.Combine(_root, "data"),
            DefaultReceiveFolder = Inbox,
            Network = _network,
            BindAddress = IPAddress.Loopback,
        });
        Assert.Equal(token, again.Settings.PairingToken);
        Assert.Contains("Ev", again.Settings.TrustedNetworks);
        Assert.NotNull(again.Devices.Authenticate(_key));
    }

    private async Task<long> Offset(string id) =>
        (await _http.GetFromJsonAsync<JsonElement>("/upload/status?id=" + id)).GetProperty("offset").GetInt64();

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met");
            await Task.Delay(50);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
