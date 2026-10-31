using System.Reflection;
using MCBShare.Core;
using Velopack;
using Velopack.Sources;

namespace MCBShare.App;

/// <summary>GitHub Releases'tan sessiz güncelleme. Yeni sürüm indirilir, uygulama boştayken uygulanıp yeniden başlatılır.</summary>
public sealed class AppUpdater(FileLog log, Func<bool> isIdle, Action quit)
{
    private const string RepositoryUrl = "https://github.com/MehmetCanBeyaztas/MCBShare";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan IdleRetry = TimeSpan.FromMinutes(1);

    private readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, null, false));
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private UpdateInfo? _ready;

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    /// <summary>Geliştirme sırasında (Velopack ile kurulmamışken) güncelleme yoktur.</summary>
    public bool IsAvailable => _manager.IsInstalled;

    public async Task RunAsync()
    {
        if (!IsAvailable) return;
        await Task.Delay(FirstCheckDelay);
        while (true)
        {
            await CheckAndDownloadAsync();
            var waited = TimeSpan.Zero;
            while (waited < CheckInterval)
            {
                await Task.Delay(IdleRetry);
                waited += IdleRetry;
                ApplyIfIdle();
            }
        }
    }

    /// <summary>Yeni sürümü denetler ve indirir. Sonucu ayarlar ekranı gösterir.</summary>
    public async Task<string> CheckAndDownloadAsync()
    {
        if (!IsAvailable) return Loc.T("UpdateDev");
        await _checkLock.WaitAsync();
        try
        {
            if (_ready is not null) return string.Format(Loc.T("UpdateReady"), _ready.TargetFullRelease.Version);

            var info = await _manager.CheckForUpdatesAsync();
            if (info is null) return Loc.T("UpdateNone");

            log.Info($"Update found: {info.TargetFullRelease.Version}");
            await _manager.DownloadUpdatesAsync(info);
            _ready = info;
            log.Info($"Update downloaded: {info.TargetFullRelease.Version}");
            return string.Format(Loc.T("UpdateReady"), info.TargetFullRelease.Version);
        }
        catch (Exception ex)
        {
            // Ağ yokken veya GitHub'a erişilemezken uygulama çalışmayı sürdürür.
            log.Error("Update check failed", ex);
            return Loc.T("UpdateFailed");
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private void ApplyIfIdle()
    {
        if (_ready is null || !isIdle()) return;
        log.Info($"Applying update: {_ready.TargetFullRelease.Version}");
        _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: true);
        quit();
    }
}
