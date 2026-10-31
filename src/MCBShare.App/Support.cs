using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Input;
using MCBShare.Core;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Networking.Connectivity;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MCBShare.App;

public sealed class Relay(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}

internal static partial class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public const uint WM_NCCALCSIZE = 0x83;
    private static readonly List<SubclassProc> FrameProcs = [];
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    public static readonly nint HWND_TOPMOST = -1;
    public const uint WM_HOTKEY = 0x312;
    public const uint MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2, DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    public delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(out POINT point);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int key);

    public static bool IsMouseButtonDown()
    {
        const int VK_LBUTTON = 1, VK_RBUTTON = 2;
        return GetAsyncKeyState(VK_LBUTTON) < 0 || GetAsyncKeyState(VK_RBUTTON) < 0;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static partial nint GetWindowLongPtr(nint hwnd, int index);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShowWindow(nint hwnd, int command);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnregisterHotKey(nint hwnd, int id);
    [LibraryImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [LibraryImport("comctl32.dll")] public static partial nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [LibraryImport("dwmapi.dll")] public static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [LibraryImport("shell32.dll")] public static partial int SHQueryUserNotificationState(out int state);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyIcon(nint icon);

#if DEBUG
    public static bool DevMode => Environment.GetEnvironmentVariable("MCBSHARE_DEV") == "1";
#else
    public static bool DevMode => false;
#endif

    /// <summary>Oyun, film, sunum veya Rahatsız Etme açıksa false döner.</summary>
    public static bool CanShowNotifications()
    {
        const int QUNS_ACCEPTS_NOTIFICATIONS = 5;
        if (DevMode) return true;
        return SHQueryUserNotificationState(out var state) != 0 || state == QUNS_ACCEPTS_NOTIFICATIONS;
    }

    /// <summary>Kenarlıksız, her zaman üstte pencere kurar ve XAML çizimi başlasın diye ekran dışında bir kez etkinleştirir.</summary>
    public static void PrepareBorderless(Microsoft.UI.Xaml.Window window, nint hwnd, long extraStyles)
    {
        var presenter = Microsoft.UI.Windowing.OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        window.AppWindow.SetPresenter(presenter);
        window.AppWindow.IsShownInSwitchers = false;
        AddExtendedStyle(hwnd, WS_EX_TOOLWINDOW | extraStyles);
        // Presenter kenarlığı kapatsa da boyutlandırma çerçevesi kalır ve XAML'in etrafında açık renkli bir şerit olarak görünür.
        // İstemci alanı tüm pencereyi kaplayınca şerit kaybolur; gölge ve yuvarlak köşeler DWM'de kalır.
        SubclassProc removeFrame = (h, msg, wParam, lParam, _, _) =>
            msg == WM_NCCALCSIZE && wParam != 0 ? 0 : DefSubclassProc(h, msg, wParam, lParam);
        FrameProcs.Add(removeFrame);
        SetWindowSubclass(hwnd, removeFrame, 2, 0);
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        RoundCorners(hwnd);
        window.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
        window.Activate();
        window.AppWindow.Hide();
    }

    public static void RoundCorners(nint hwnd)
    {
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
    }

    public static void AddExtendedStyle(nint hwnd, long styles) =>
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)(GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | styles));
}

public sealed class HotkeyHost : IDisposable
{
    public const int PanelHotkey = 1, SendImageHotkey = 2;
    private const uint VK_M = 0x4D, VK_Q = 0x51;

    private readonly nint _hwnd;
    private readonly Native.SubclassProc _proc;

    public HotkeyHost(nint hwnd)
    {
        _hwnd = hwnd;
        _proc = WndProc;
        Native.SetWindowSubclass(hwnd, _proc, 1, 0);
        const uint mods = Native.MOD_WIN | Native.MOD_SHIFT | Native.MOD_NOREPEAT;
        Native.RegisterHotKey(hwnd, PanelHotkey, mods, VK_M);
        Native.RegisterHotKey(hwnd, SendImageHotkey, mods, VK_Q);
    }

    public event Action<int>? Pressed;

    public void Dispose()
    {
        Native.UnregisterHotKey(_hwnd, PanelHotkey);
        Native.UnregisterHotKey(_hwnd, SendImageHotkey);
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == Native.WM_HOTKEY)
        {
            Pressed?.Invoke((int)wParam);
            return 0;
        }
        return Native.DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}

public sealed class WindowsNetworkIdentity : INetworkIdentity
{
    public WindowsNetworkIdentity() =>
        NetworkInformation.NetworkStatusChanged += _ => Changed?.Invoke();

    public event Action? Changed;

    public string? CurrentNetworkName
    {
        get
        {
            var profile = NetworkInformation.GetInternetConnectionProfile()
                ?? NetworkInformation.GetConnectionProfiles()
                    .FirstOrDefault(p => p.GetNetworkConnectivityLevel() >= NetworkConnectivityLevel.LocalAccess);
            return profile?.ProfileName;
        }
    }
}

public static class TrayIcons
{
    /// <summary>Görev çubuğu temasına uygun tek renkli ikon. Sunucu kapalıyken içi boş çizilir.</summary>
    public static Icon Create(bool active, double? progress = null)
    {
        var lightTaskbar = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is 1;
        var color = lightTaskbar ? Color.FromArgb(30, 30, 30) : Color.White;

        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, 2.5f);
            using var brush = new SolidBrush(color);
            g.DrawRectangle(pen, 3, 6, 10, 20);
            if (active) g.FillRectangle(brush, 19, 6, 10, 15);
            else g.DrawRectangle(pen, 19, 6, 10, 15);
            g.FillRectangle(brush, 21, 24, 6, 2.5f);
            g.DrawLine(pen, 14, 16, 18, 16);
            if (progress is { } p)
            {
                using var track = new SolidBrush(Color.FromArgb(110, color));
                using var fill = new SolidBrush(Color.FromArgb(76, 194, 255));
                g.FillRectangle(track, 0, 27, 32, 5);
                g.FillRectangle(fill, 0, 27, (float)(32 * p), 5);
            }
        }

        var handle = bitmap.GetHicon();
        using var temporary = Icon.FromHandle(handle);
        var icon = (Icon)temporary.Clone();
        Native.DestroyIcon(handle);
        return icon;
    }
}

public static class QrImages
{
    public static async Task<BitmapImage> CreateAsync(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        await image.SetSourceAsync(stream);
        return image;
    }
}

public static class ClipboardFiles
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"];

    /// <summary>Dosyayı panoya koyar; fotoğraflar hem dosya hem görüntü olarak konur.</summary>
    public static async Task CopyAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetStorageItems([file]);
        if (ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    public static async Task<string?> ReadTextAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
        }
        catch (COMException)
        {
            // Pano o an başka bir uygulamada kilitli olabilir; kutu boş açılır.
            return null;
        }
    }

    public static async Task<string?> SaveClipboardImageAsync(string folder)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Bitmap)) return null;

        var reference = await content.GetBitmapAsync();
        using var source = await reference.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(source);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"pano-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        await using var output = File.Create(path);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output.AsRandomAccessStream());
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        return path;
    }

    public static string? LatestScreenshot()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        if (!Directory.Exists(folder)) return null;
        return new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => ImageExtensions.Contains(f.Extension.ToLowerInvariant()))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }
}

/// <summary>İsteğe bağlı HEIC → JPG. Windows'ta HEIF codec'i yoksa dönüşüm hata verir ve özgün dosya kalır.</summary>
public sealed class HeicProcessor(Func<AppSettings> settings) : IReceivedFileProcessor
{
    public async Task<string> ProcessAsync(string path, CancellationToken ct)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!settings().ConvertHeicToJpg || extension is not (".heic" or ".heif")) return path;

        var target = FileNames.Unique(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".jpg");
        var date = File.GetLastWriteTimeUtc(path);
        await using (var input = File.OpenRead(path))
        await using (var output = File.Create(target))
        {
            var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output.AsRandomAccessStream());
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
        }
        File.SetLastWriteTimeUtc(target, date);
        File.Delete(path);
        return target;
    }
}

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("MCBShare") is not null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("MCBShare", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("MCBShare", false);
        }
    }
}

public static class Formats
{
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    public static string Ago(DateTimeOffset at)
    {
        var elapsed = DateTimeOffset.UtcNow - at;
        if (elapsed.TotalMinutes < 1) return Loc.T("JustNow");
        if (elapsed.TotalHours < 1) return string.Format(Loc.T("MinutesAgo"), (int)elapsed.TotalMinutes);
        if (elapsed.TotalDays < 1) return string.Format(Loc.T("HoursAgo"), (int)elapsed.TotalHours);
        return string.Format(Loc.T("DaysAgo"), (int)elapsed.TotalDays);
    }
}

public static class Loc
{
    private static bool _turkish = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";

    private static readonly Dictionary<string, (string Tr, string En)> Texts = new()
    {
        ["Settings"] = ("Ayarlar", "Settings"),
        ["Exit"] = ("Çıkış", "Exit"),
        ["OpenFolder"] = ("Klasörü aç", "Open folder"),
        ["ConnectPhone"] = ("Telefon bağla", "Connect phone"),
        ["Empty"] = ("Henüz dosya yok. Telefonu bağlayıp bir şey gönderin.", "No files yet. Connect your phone and send something."),
        ["TrustNetwork"] = ("Bu ağa güven", "Trust this network"),
        ["StateRunning"] = ("Hazır", "Ready"),
        ["StateStopped"] = ("Durduruldu", "Stopped"),
        ["StateUntrusted"] = ("Bu ağ onaylı değil, sunucu kapalı", "This network isn't approved, server is off"),
        ["StatePortInUse"] = ("Port başka bir uygulamada kullanımda", "The port is used by another app"),
        ["StateFailed"] = ("Sunucu başlatılamadı", "Server couldn't start"),
        ["NoNetwork"] = ("Ağ bağlantısı yok", "No network connection"),
        ["Copied"] = ("Kopyalandı ✓", "Copied ✓"),
        ["Copy"] = ("Kopyala", "Copy"),
        ["ShowInFolder"] = ("Klasörde göster", "Show in folder"),
        ["Delete"] = ("Sil", "Delete"),
        ["Back"] = ("Geri", "Back"),
        ["Cancel"] = ("İptal", "Cancel"),
        ["PairHint"] = ("Telefon kamerasıyla okutun, cihaza bir ad verin.", "Scan with your phone's camera and name the device."),
        ["WaitingPhone"] = ("Telefon bekleniyor…", "Waiting for phone…"),
        ["Downloading"] = ("İndiriliyor %{0}", "Downloading {0}%"),
        ["Downloaded"] = ("İndirildi ✓", "Downloaded ✓"),
        ["Expired"] = ("Kodun süresi doldu", "Code expired"),
        ["Remaining"] = ("Kalan süre {0}", "{0} left"),
        ["FilesCount"] = ("{0} dosya", "{0} files"),
        ["JustNow"] = ("Az önce", "Just now"),
        ["MinutesAgo"] = ("{0} dk önce", "{0} min ago"),
        ["HoursAgo"] = ("{0} sa önce", "{0} h ago"),
        ["DaysAgo"] = ("{0} gün önce", "{0} d ago"),
        ["FromPhone"] = ("Telefondan alındı", "Received from phone"),
        ["FromDevice"] = ("{0} cihazından alındı", "Received from {0}"),
        ["Receiving"] = ("{0}/{1} alınıyor…", "Receiving {0}/{1}…"),
        ["ReceivedCheck"] = ("✓ Alındı", "✓ Received"),
        ["ReceivedMany"] = ("{0} dosya alındı", "{0} files received"),
        ["AlreadyHere"] = ("Zaten bu bilgisayarda", "Already on this PC"),
        ["TransferFailed"] = ("Aktarım kesildi", "Transfer interrupted"),
        ["About"] = ("Hakkında", "About"),
        ["Version"] = ("Sürüm {0}", "Version {0}"),
        ["CheckUpdates"] = ("Güncellemeleri denetle", "Check for updates"),
        ["UpdateChecking"] = ("Denetleniyor…", "Checking…"),
        ["UpdateNone"] = ("Uygulama güncel", "You're up to date"),
        ["UpdateReady"] = ("{0} indirildi. Pencereler kapanınca kurulacak.", "{0} downloaded. It installs once windows are closed."),
        ["UpdateFailed"] = ("Denetlenemedi. Ayrıntı logda.", "Couldn't check. See the log."),
        ["UpdateDev"] = ("Geliştirme sürümünde güncelleme yok", "No updates in a development build"),
        ["TransferWaiting"] = ("Telefondan veri bekleniyor…", "Waiting for data from phone…"),
        ["SendText"] = ("Telefona metin gönder", "Send text to phone"),
        ["TextTitle"] = ("Telefona metin", "Text to phone"),
        ["TextPlaceholder"] = ("Metin veya link yazın", "Type text or a link"),
        ["SendToPhone"] = ("Telefona gönder", "Send to phone"),
        ["ShowTextQr"] = ("QR göster", "Show QR"),
        ["TextWaiting"] = ("Gönderildi. Telefonda MCBShare sayfası açıkken görünür (15 dk).", "Sent. It appears while the MCBShare page is open on your phone (15 min)."),
        ["TextDelivered"] = ("{0} aldı ✓", "{0} received it ✓"),
        ["TextQrHint"] = ("Kamerayla okutun. Metin QR'ın içindedir, ağ gerekmez.", "Scan with the camera. The text is inside the QR, no network needed."),
        ["TextTooLong"] = ("Metin QR için çok uzun. \"Telefona gönder\"i kullanın.", "Text is too long for a QR. Use \"Send to phone\"."),
        ["TransferStalled"] = ("Telefondan veri gelmiyor. Sayfa açık kalırsa devam eder.", "No data from phone. It resumes if the page stays open."),
        ["ClipboardReceived"] = ("Telefondan metin panoya kopyalandı", "Text from phone copied to clipboard"),
        ["General"] = ("Genel", "General"),
        ["ReceiveFolder"] = ("Kayıt klasörü", "Save folder"),
        ["Change"] = ("Değiştir", "Change"),
        ["StartWithWindows"] = ("Windows ile başlat", "Start with Windows"),
        ["ShowToast"] = ("Aktarım kutusunu göster", "Show transfer box"),
        ["ConvertHeic"] = ("HEIC dosyalarını JPG'ye dönüştür", "Convert HEIC files to JPG"),
        ["ByDate"] = ("Tarihe göre alt klasörlere ayır", "Sort into folders by date"),
        ["SkipDuplicates"] = ("Aynı dosyayı tekrar kaydetme", "Don't save the same file twice"),
        ["Retention"] = ("Liste ve giden kutusunu temizleme süresi (gün)", "Clear list and outbox after (days)"),
        ["Language"] = ("Dil (yeniden başlatınca uygulanır)", "Language (applies after restart)"),
        ["SystemLanguage"] = ("Windows'un dili", "Windows language"),
        ["Networks"] = ("Güvenilir ağlar", "Trusted networks"),
        ["Devices"] = ("Cihazlar", "Devices"),
        ["Remove"] = ("Kaldır", "Remove"),
        ["NoDevices"] = ("Kayıtlı cihaz yok", "No devices yet"),
        ["NewPairCode"] = ("Eşleşme kodunu yenile", "Renew pairing code"),
        ["Shortcut"] = ("iOS Kısayolu kurulumu", "iOS Shortcut setup"),
        ["ShortcutCreate"] = ("Kısayol için anahtar oluştur", "Create key for Shortcut"),
        ["ShortcutName"] = ("Kısayol cihazı", "Shortcut device"),
        ["ShortcutSteps"] = (
            "1. Kısayollar'da yeni kısayol oluşturun; ayrıntılardan \"Paylaşım Sayfasında Göster\"i açın, Fotoğraf ve Dosya seçin.\n2. \"Her Öğe için Tekrarla\" ekleyin.\n3. İçine \"URL'nin İçeriğini Al\" ekleyin. URL: aşağıdaki adres + ?name= ve Tekrar Öğesi'nin adı. Yöntem: POST, İstek Gövdesi: Dosya → Tekrar Öğesi.\n4. Üstbilgi ekleyin: X-MCB-Key = aşağıdaki anahtar.\n5. Döngüden sonra \"Bildirim Göster\": PC'ye gönderildi ✓\n6. İlk çalıştırmada ağ izni için \"Her Zaman İzin Ver\"i seçin.",
            "1. Create a new shortcut; in details enable \"Show in Share Sheet\" and choose Photos and Files.\n2. Add \"Repeat with Each\".\n3. Inside it add \"Get Contents of URL\". URL: the address below + ?name= and the Repeat Item's name. Method: POST, Request Body: File → Repeat Item.\n4. Add a header: X-MCB-Key = the key below.\n5. After the loop add \"Show Notification\": Sent to PC ✓\n6. On first run choose \"Always Allow\" for network access."),
        ["Address"] = ("Adres", "Address"),
        ["Key"] = ("Anahtar (yalnızca şimdi görünür)", "Key (shown only now)"),
        ["Logs"] = ("Log klasörünü aç", "Open log folder"),
    };

    public static void Apply(string language)
    {
        if (language is "tr" or "en") _turkish = language == "tr";
    }

    public static string T(string key) => Texts.TryGetValue(key, out var text) ? (_turkish ? text.Tr : text.En) : key;

    public static string StateText(ServerState state) => state switch
    {
        ServerState.Running => T("StateRunning"),
        ServerState.UntrustedNetwork => T("StateUntrusted"),
        ServerState.PortInUse => T("StatePortInUse"),
        ServerState.Failed => T("StateFailed"),
        _ => T("StateStopped"),
    };
}
