using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MCBShare.Core;

/// <summary>Bilgisayarın bağlı olduğu ağın adını verir. Windows tarafı uygulama katmanındadır.</summary>
public interface INetworkIdentity
{
    string? CurrentNetworkName { get; }
    event Action? Changed;
}

public enum ServerState { Stopped, UntrustedNetwork, Running, PortInUse, Failed }

public sealed class CoreOptions
{
    public required string DataFolder { get; init; }
    public required string DefaultReceiveFolder { get; init; }
    public required INetworkIdentity Network { get; init; }
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public Func<string, long>? FreeSpace { get; init; }
    public IReceivedFileProcessor? Processor { get; init; }
}

/// <summary>Çekirdeğin tek giriş noktası: sunucuyu güvenilir ağa göre açıp kapatır, servisleri dışarı verir.</summary>
public sealed class MCBShareCore : IAsyncDisposable
{
    private readonly CoreOptions _options;
    private readonly JsonStore<AppSettings> _settingsStore;
    private readonly AttemptLimiter _limiter;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private WebApplication? _app;

    public MCBShareCore(CoreOptions options)
    {
        _options = options;
        System.IO.Directory.CreateDirectory(options.DataFolder);
        _settingsStore = new JsonStore<AppSettings>(Path.Combine(options.DataFolder, "settings.json"));
        Settings = _settingsStore.Load();
        if (string.IsNullOrEmpty(Settings.ReceiveFolder)) Settings.ReceiveFolder = options.DefaultReceiveFolder;
        if (string.IsNullOrEmpty(Settings.PairingToken)) Settings.PairingToken = Secrets.NewKey();
        _settingsStore.Save(Settings);

        Log = new FileLog(Path.Combine(options.DataFolder, "logs"), options.Time);
        Devices = new DeviceRegistry(new JsonStore<DeviceFile>(Path.Combine(options.DataFolder, "devices.json")), options.Time);
        History = new ShareHistory(new JsonStore<HistoryFile>(Path.Combine(options.DataFolder, "history.json")), options.Time);
        Uploads = new UploadService(() => Settings, History, Log, options.Time, options.FreeSpace, options.Processor);
        Shares = new ShareService(options.Time, Log);
        Clipboard = new ClipboardExchange(options.Time, Log);
        OutboxFolder = Path.Combine(options.DataFolder, "outbox");
        _limiter = new AttemptLimiter(options.Time);
        options.Network.Changed += OnNetworkChanged;
    }

    public AppSettings Settings { get; }
    public FileLog Log { get; }
    public DeviceRegistry Devices { get; }
    public ShareHistory History { get; }
    public UploadService Uploads { get; }
    public ShareService Shares { get; }
    public ClipboardExchange Clipboard { get; }
    public string OutboxFolder { get; }
    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? CurrentNetworkName => _options.Network.CurrentNetworkName;
    public bool IsCurrentNetworkTrusted =>
        CurrentNetworkName is { } name && Settings.TrustedNetworks.Contains(name, StringComparer.OrdinalIgnoreCase);

    public event Action? StateChanged;

    public void SaveSettings() => _settingsStore.Save(Settings);

    /// <summary>Sunucunun telefondan görülen kök adresi.</summary>
    public string? BaseUrl =>
        NetworkAddresses.FindLanAddress() is { } ip ? $"http://{ip}:{Settings.Port}" : null;

    public string? PairingUrl => BaseUrl is { } url ? $"{url}/#pair={Settings.PairingToken}" : null;

    /// <summary>Anahtar adresin # kısmındadır; sunucuya ve kayıtlara gitmez.</summary>
    public string? ShortcutSetupUrl(string key) => BaseUrl is { } url ? $"{url}/shortcut#key={Uri.EscapeDataString(key)}" : null;

    public string? ShareUrl(string code) => BaseUrl is { } url ? $"{url}/d/{code}" : null;

    public void RegeneratePairingToken()
    {
        Settings.PairingToken = Secrets.NewKey();
        SaveSettings();
        StateChanged?.Invoke();
    }

    public async Task TrustCurrentNetworkAsync()
    {
        if (CurrentNetworkName is { } name && !IsCurrentNetworkTrusted)
        {
            Settings.TrustedNetworks.Add(name);
            SaveSettings();
        }
        await EvaluateAsync();
    }

    public async Task UntrustNetworkAsync(string name)
    {
        Settings.TrustedNetworks.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
        await EvaluateAsync();
    }

    /// <summary>Ağ güvenilirse sunucuyu açar, değilse kapatır. Port değişiminden sonra da çağrılır.</summary>
    public async Task EvaluateAsync(bool restart = false)
    {
        await _stateLock.WaitAsync();
        try
        {
            if (restart) await StopServerAsync();
            if (IsCurrentNetworkTrusted)
            {
                if (_app is null) await StartServerAsync();
            }
            else
            {
                await StopServerAsync();
                SetState(ServerState.UntrustedNetwork);
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>Saklama süresini aşan liste kayıtlarını, giden kutusunu ve yarım yüklemeleri temizler.</summary>
    public void RunCleanup()
    {
        History.Prune(Settings.RetentionDays);
        Uploads.CleanStaleParts(TimeSpan.FromDays(7));
        if (Settings.RetentionDays > 0 && System.IO.Directory.Exists(OutboxFolder))
        {
            var limit = _options.Time.GetUtcNow().UtcDateTime.AddDays(-Settings.RetentionDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(OutboxFolder))
            {
                if (File.GetLastWriteTimeUtc(file) < limit)
                {
                    try { File.Delete(file); } catch (IOException) { }
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _options.Network.Changed -= OnNetworkChanged;
        await _stateLock.WaitAsync();
        try { await StopServerAsync(); }
        finally { _stateLock.Release(); }
    }

    private async void OnNetworkChanged()
    {
        try
        {
            await EvaluateAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Network change handling failed", ex);
        }
    }

    private async Task StartServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = null;
            k.Listen(_options.BindAddress, Settings.Port);
        });
        var app = builder.Build();
        Endpoints.Map(app, this, _limiter);
        try
        {
            await app.StartAsync();
        }
        catch (Exception ex)
        {
            await app.DisposeAsync();
            var inUse = ex is IOException { InnerException: SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } }
                or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse };
            Log.Error("Server failed to start", ex);
            SetState(inUse ? ServerState.PortInUse : ServerState.Failed);
            return;
        }
        _app = app;
        Log.Info($"Server started on port {Settings.Port}");
        SetState(ServerState.Running);
    }

    private async Task StopServerAsync()
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        // Sürmekte olan yüklemeler parçalı kaldığı yerden devam edebildiği için uzun beklenmez.
        await app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        await app.DisposeAsync();
        Log.Info("Server stopped");
        SetState(ServerState.Stopped);
    }

    private void SetState(ServerState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke();
    }
}

public static class NetworkAddresses
{
    /// <summary>Varsayılan ağ geçidi olan gerçek bağdaştırıcının IPv4 adresi. VPN ve sanal kartlar elenir.</summary>
    public static IPAddress? FindLanAddress()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet
                && !IsVirtual(n))
            .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .Select(n => n.GetIPProperties())
            .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(p => p.UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && IsLocal(a.Address))
            .Select(a => a.Address)
            .FirstOrDefault();
    }

    public static bool IsLocal(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }

    private static bool IsVirtual(NetworkInterface n)
    {
        var text = (n.Name + " " + n.Description).ToLowerInvariant();
        return new[] { "virtual", "vmware", "hyper-v", "vethernet", "vpn", "tap-", "wireguard", "tailscale", "bluetooth" }
            .Any(text.Contains);
    }
}

internal static class Endpoints
{
    private const string KeyHeader = "X-MCB-Key";

    private sealed record RegisterBody(string? Token, string? Name);
    private sealed record ClipboardBody(string? Text);
    private sealed record ClientLogBody(string? Message);

    public static void Map(WebApplication app, MCBShareCore core, AttemptLimiter limiter)
    {
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (!NetworkAddresses.IsLocal(ctx.Connection.RemoteIpAddress))
            {
                await Error(ctx, 403, "not_local", "Local network only");
                return;
            }
            if (limiter.IsBlocked(Ip(ctx)))
            {
                await Error(ctx, 429, "too_many_attempts", "Too many failed attempts");
                return;
            }
            try
            {
                await next(ctx);
            }
            catch (ApiException ex)
            {
                core.Log.Info($"Rejected {ctx.Request.Method} {ctx.Request.Path}: {ctx.Response.StatusCode = ex.Status} {ex.Error}");
                await Error(ctx, ex.Status, ex.Error, ex.Message, ex.Offset);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or BadHttpRequestException)
            {
                core.Log.Info($"Connection lost on {ctx.Request.Method} {ctx.Request.Path}: {ex.GetType().Name}");
                // Bağlantı kopması; yanıt verilecek kimse kalmadı.
                ctx.Abort();
            }
            catch (Exception ex)
            {
                core.Log.Error($"Unexpected error on {ctx.Request.Method} {ctx.Request.Path}", ex);
                await Error(ctx, 500, "internal_error", "Unexpected server error");
            }
        });

        app.MapGet("/", () => WebAsset("index.html", "text/html; charset=utf-8"));
        app.MapGet("/shortcut", () => WebAsset("shortcut.html", "text/html; charset=utf-8"));
        app.MapGet("/manifest.webmanifest", () => WebAsset("manifest.webmanifest", "application/manifest+json"));
        app.MapGet("/icon.svg", () => WebAsset("icon.svg", "image/svg+xml"));

        app.MapPost("/api/register", async (HttpContext ctx) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<RegisterBody>(ctx.RequestAborted);
            if (body?.Token is null || !Secrets.EqualsConstantTime(body.Token, core.Settings.PairingToken))
            {
                limiter.RecordFailure(Ip(ctx));
                return Failure(401, "bad_token", "Pairing code is not valid");
            }
            var (device, key) = core.Devices.Register(body.Name ?? "");
            core.Log.Info($"Device registered: {device.Name}");
            return Results.Json(new { deviceId = device.Id, name = device.Name, key });
        });

        app.MapGet("/api/me", (HttpContext ctx) =>
        {
            var device = Authenticate(ctx, core, limiter);
            return Results.Json(new { deviceId = device.Id, name = device.Name });
        });

        app.MapPost("/upload", async (HttpContext ctx) =>
        {
            var device = Authenticate(ctx, core, limiter);
            var q = ctx.Request.Query;
            string? headerName = ctx.Request.Headers["X-MCB-Filename"];
            var name = q["name"].FirstOrDefault()
                ?? (headerName is null ? null : Uri.UnescapeDataString(headerName));
            if (string.IsNullOrWhiteSpace(name))
                throw new ApiException(400, "bad_request", "name is required");

            var request = new UploadRequest(
                name,
                q["id"].FirstOrDefault(),
                ParseLong(q["offset"]) ?? 0,
                ParseLong(q["size"]),
                ParseLong(q["modified"]),
                (int?)ParseLong(q["batchIndex"]),
                (int?)ParseLong(q["batchTotal"]));

            var result = await core.Uploads.ReceiveAsync(device, request, ctx.Request.Body, ctx.Request.ContentLength, ctx.RequestAborted);
            return Results.Json(new { complete = result.Complete, offset = result.Offset, name = result.Name, duplicate = result.Duplicate });
        });

        app.MapGet("/upload/status", (HttpContext ctx) =>
        {
            var device = Authenticate(ctx, core, limiter);
            var id = ctx.Request.Query["id"].FirstOrDefault() ?? throw new ApiException(400, "bad_request", "id is required");
            return Results.Json(new { offset = core.Uploads.GetOffset(device, id) });
        });

        app.MapPost("/api/clipboard", async (HttpContext ctx) =>
        {
            var device = Authenticate(ctx, core, limiter);
            if (ctx.Request.ContentLength > ClipboardExchange.MaxBytes)
                throw new ApiException(413, "too_large", "Text is too large");
            var body = await ctx.Request.ReadFromJsonAsync<ClipboardBody>(ctx.RequestAborted);
            if (string.IsNullOrEmpty(body?.Text))
                throw new ApiException(400, "bad_request", "text is required");
            if (Encoding.UTF8.GetByteCount(body.Text) > ClipboardExchange.MaxBytes)
                throw new ApiException(413, "too_large", "Text is too large");
            core.Clipboard.Receive(device.Name, body.Text);
            return Results.Json(new { ok = true });
        });

        app.MapGet("/api/clipboard", (HttpContext ctx) =>
        {
            var device = Authenticate(ctx, core, limiter);
            var (text, at) = core.Clipboard.FetchForDevice(device.Id, device.Name);
            return Results.Json(new { text, at = at?.ToUnixTimeMilliseconds() });
        });

        app.MapPost("/api/clientlog", async (HttpContext ctx) =>
        {
            var device = Authenticate(ctx, core, limiter);
            if (ctx.Request.ContentLength > 4096)
                throw new ApiException(413, "too_large", "Message is too large");
            var body = await ctx.Request.ReadFromJsonAsync<ClientLogBody>(ctx.RequestAborted);
            var text = string.Concat((body?.Message ?? "").Where(c => !char.IsControl(c)));
            if (text.Length > 500) text = text[..500];
            core.Log.Info($"Phone [{device.Name}]: {text}");
            return Results.Json(new { ok = true });
        });

        app.MapGet("/d/{code}", (HttpContext ctx, string code) =>
        {
            if (core.Shares.Get(code) is not { } share)
                return DeadShare(ctx, limiter);
            return share.Files.Count == 1
                ? Download(core, code, 0)
                : Results.Content(SharePages.List(share, ctx.Request.Headers.AcceptLanguage), "text/html; charset=utf-8");
        });

        app.MapGet("/d/{code}/{index:int}", (HttpContext ctx, string code, int index) =>
            core.Shares.Get(code) is null ? DeadShare(ctx, limiter) : Download(core, code, index));
    }

    private static IResult Download(MCBShareCore core, string code, int index)
    {
        if (core.Shares.BeginDownload(code, index) is not { } file)
            return Failure(404, "not_found", "File not found");
        FileStream stream;
        try
        {
            stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 256 * 1024, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            core.Log.Error("Shared file could not be opened", ex);
            return Failure(404, "not_found", "File not found");
        }
        var tracked = new ProgressStream(stream, (start, end) => core.Shares.ReportProgress(code, index, start, end));
        return Results.File(tracked, "application/octet-stream", file.Name, enableRangeProcessing: true);
    }

    private static IResult DeadShare(HttpContext ctx, AttemptLimiter limiter)
    {
        limiter.RecordFailure(Ip(ctx));
        return Results.Content(SharePages.Expired(ctx.Request.Headers.AcceptLanguage), "text/html; charset=utf-8", statusCode: 410);
    }

    private static Device Authenticate(HttpContext ctx, MCBShareCore core, AttemptLimiter limiter)
    {
        var device = core.Devices.Authenticate(ctx.Request.Headers[KeyHeader].FirstOrDefault());
        if (device is null)
        {
            limiter.RecordFailure(Ip(ctx));
            throw new ApiException(401, "bad_key", "Device key is not valid");
        }
        return device;
    }

    private static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "";

    private static long? ParseLong(string? value) => long.TryParse(value, out var n) ? n : null;

    private static IResult Failure(int status, string error, string message) =>
        Results.Json(new { error, message }, statusCode: status);

    private static async Task Error(HttpContext ctx, int status, string error, string message, long? offset = null)
    {
        if (ctx.Response.HasStarted) { ctx.Abort(); return; }
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { error, message, offset });
    }

    private static IResult WebAsset(string name, string contentType)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + name);
        return stream is null ? Results.NotFound() : Results.Stream(stream, contentType);
    }
}

internal static class SharePages
{
    private const string Style = """
        <meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <style>
        :root{color-scheme:light dark;--bg:#f2f2f7;--card:#fff;--text:#000;--muted:#6c6c70;--accent:#0a84ff}
        @media(prefers-color-scheme:dark){:root{--bg:#000;--card:#1c1c1e;--text:#fff;--muted:#98989f}}
        body{margin:0;padding:24px 16px;background:var(--bg);color:var(--text);font:17px -apple-system,system-ui,sans-serif}
        h1{font-size:28px;margin:0 0 16px}
        a.row{display:flex;justify-content:space-between;gap:12px;padding:14px 16px;background:var(--card);border-radius:12px;margin-bottom:8px;color:var(--accent);text-decoration:none;overflow-wrap:anywhere}
        small{color:var(--muted);white-space:nowrap}p{color:var(--muted)}
        </style>
        """;

    private static bool IsTurkish(string? acceptLanguage) =>
        acceptLanguage?.StartsWith("tr", StringComparison.OrdinalIgnoreCase) == true;

    public static string List(ShareSnapshot share, string? acceptLanguage)
    {
        var tr = IsTurkish(acceptLanguage);
        var html = new StringBuilder("<!doctype html><title>MCBShare</title>").Append(Style);
        html.Append("<h1>").Append(tr ? $"{share.Files.Count} dosya" : $"{share.Files.Count} files").Append("</h1>");
        for (var i = 0; i < share.Files.Count; i++)
        {
            var file = share.Files[i];
            html.Append($"<a class=row href=\"/d/{share.Code}/{i}\">")
                .Append(HtmlEncoder.Default.Encode(file.Name))
                .Append("<small>").Append(FormatSize(file.Size)).Append("</small></a>");
        }
        return html.ToString();
    }

    public static string Expired(string? acceptLanguage)
    {
        var tr = IsTurkish(acceptLanguage);
        return "<!doctype html><title>MCBShare</title>" + Style
            + (tr
                ? "<h1>Bu kodun süresi doldu</h1><p>PC'de dosyayı yeniden paylaşıp yeni QR'ı okutun.</p>"
                : "<h1>This code has expired</h1><p>Share the file again on your PC and scan the new QR code.</p>");
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };
}
