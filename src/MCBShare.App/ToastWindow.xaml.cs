using System.Diagnostics;
using MCBShare.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.FileProperties;
using WinRT.Interop;

namespace MCBShare.App;

/// <summary>Orta alttaki aktarım kutusu. Odak çalmaz; panele ve kayıt listesine bağlı değildir.</summary>
public sealed partial class ToastWindow : Window
{
    private const int LogicalWidth = 360, LogicalHeight = 72, ScreenMargin = 12;
    private const double AnimationMs = 180;
    private static readonly TimeSpan VisibleFor = TimeSpan.FromSeconds(4);

    private readonly nint _handle;
    private readonly DispatcherTimer _hideTimer = new() { Interval = VisibleFor };
    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _stallTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private long _animationStart;
    private bool _opening;
    private bool _visible;
    private bool _hovering;
    private RectInt32 _target;
    private int _completedInRun;
    private string? _lastPath;

    public ToastWindow()
    {
        InitializeComponent();
        _handle = WindowNative.GetWindowHandle(this);

        Native.PrepareBorderless(this, _handle, Native.WS_EX_NOACTIVATE);

        CopyButton.Content = Loc.T("Copy");
        _hideTimer.Tick += (_, _) => Dismiss();
        _animation.Tick += (_, _) => StepAnimation();
        _stallTimer.Tick += (_, _) => OnStalled();
    }

    /// <summary>Telefon parçalar arasında susarsa sunucuya hiçbir şey ulaşmaz; kutu son yüzdede kalmamalı.</summary>
    private void OnStalled()
    {
        _stallTimer.Stop();
        Progress.Visibility = Visibility.Collapsed;
        Subtitle.Text = Loc.T("TransferStalled");
        RestartHideTimer();
    }

    public void OnTransfer(TransferUpdate update)
    {
        // Görünen kutu her durumda güncellenir; aksi hâlde Windows meşgule geçince eski yüzdede donar.
        if (!_visible && (!App.Current.Core.Settings.ShowTransferNotifications || !Native.CanShowNotifications()))
            return;

        _stallTimer.Stop();
        if (update.State == TransferState.Running) _stallTimer.Start();

        var hasBatch = update.BatchTotal is > 1;
        Progress.Visibility = Visibility.Collapsed;
        CopyButton.Visibility = Visibility.Collapsed;
        switch (update.State)
        {
            case TransferState.Running:
                TitleText.Text = hasBatch
                    ? string.Format(Loc.T("Receiving"), update.BatchIndex, update.BatchTotal)
                    : update.FileName;
                Subtitle.Text = FromText(update);
                if (update.Total is > 0)
                {
                    Progress.Visibility = Visibility.Visible;
                    Progress.Value = (double)update.Received / update.Total.Value;
                    Subtitle.Text = $"%{(int)(Progress.Value * 100)}";
                }
                _hideTimer.Stop();
                break;

            case TransferState.Completed:
                _completedInRun++;
                _lastPath = update.FinalPath;
                var more = hasBatch && update.BatchIndex < update.BatchTotal;
                TitleText.Text = more
                    ? string.Format(Loc.T("Receiving"), update.BatchIndex, update.BatchTotal)
                    : _completedInRun > 1 ? string.Format(Loc.T("ReceivedMany"), _completedInRun) : update.FileName;
                Subtitle.Text = _completedInRun > 1 && !more ? FromText(update) : Loc.T("ReceivedCheck");
                if (!more && _completedInRun == 1) CopyButton.Visibility = Visibility.Visible;
                if (update.FinalPath is not null) _ = LoadPreviewAsync(update.FinalPath);
                RestartHideTimer();
                break;

            case TransferState.Duplicate:
                _lastPath = update.FinalPath;
                TitleText.Text = update.FileName;
                Subtitle.Text = Loc.T("AlreadyHere");
                RestartHideTimer();
                break;

            case TransferState.Failed:
                TitleText.Text = update.FileName;
                Subtitle.Text = Loc.T("TransferFailed");
                RestartHideTimer();
                break;
        }
        Open();
    }

    public void ShowMessage(string message)
    {
        if (!Native.CanShowNotifications()) return;
        _lastPath = null;
        TitleText.Text = "MCBShare";
        Subtitle.Text = message;
        Progress.Visibility = Visibility.Collapsed;
        CopyButton.Visibility = Visibility.Collapsed;
        Preview.Source = null;
        RestartHideTimer();
        Open();
    }

    private static string FromText(TransferUpdate update) =>
        string.IsNullOrEmpty(update.DeviceName) ? Loc.T("FromPhone") : string.Format(Loc.T("FromDevice"), update.DeviceName);

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        if (!_hovering) _hideTimer.Start();
    }

    private void Open()
    {
        if (_visible) return;
        _visible = true;
        _target = ComputeTarget();
        StartAnimation(opening: true);
    }

    private void Dismiss()
    {
        _hideTimer.Stop();
        if (_hovering || !_visible) return;
        _visible = false;
        StartAnimation(opening: false);
    }

    private void StartAnimation(bool opening)
    {
        _opening = opening;
        _animationStart = Stopwatch.GetTimestamp();
        if (opening)
        {
            Apply(1);
            Native.ShowWindow(_handle, Native.SW_SHOWNOACTIVATE);
        }
        _animation.Start();
    }

    private void StepAnimation()
    {
        var t = Math.Min(1, Stopwatch.GetElapsedTime(_animationStart).TotalMilliseconds / AnimationMs);
        var eased = 1 - Math.Pow(1 - t, 3);
        var fraction = _opening ? eased : 1 - eased;
        Apply(Math.Max(1, (int)(_target.Height * fraction)));
        if (t < 1) return;

        _animation.Stop();
        if (!_opening)
        {
            Native.ShowWindow(_handle, Native.SW_HIDE);
            Preview.Source = null;
            _completedInRun = 0;
        }
    }

    /// <summary>Alt kenar sabit kalır, yalnızca yükseklik değişir.</summary>
    private void Apply(int height) =>
        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, _target.X, _target.Y + _target.Height - height, _target.Width, height, Native.SWP_NOACTIVATE);

    /// <summary>Windows bildirimleri gibi her zaman ana ekranda, görev çubuğundan biraz yukarıda açılır.</summary>
    private RectInt32 ComputeTarget()
    {
        var area = DisplayArea.Primary.WorkArea;
        // Pencere son kez başka DPI'lı bir ekranda kalmış olabilir; ölçek hedef ekrandan okunur.
        var monitor = Native.MonitorFromPoint(new Native.POINT { X = area.X, Y = area.Y }, 1);
        var scale = Native.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : Native.GetDpiForWindow(_handle) / 96.0;
        var width = (int)(LogicalWidth * scale);
        var height = (int)(LogicalHeight * scale);
        var margin = (int)(ScreenMargin * scale);
        return new RectInt32(area.X + (area.Width - width) / 2, area.Y + area.Height - height - margin, width, height);
    }

    private async Task LoadPreviewAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 64, ThumbnailOptions.ResizeThumbnail);
            var image = new BitmapImage();
            await image.SetSourceAsync(thumb);
            Preview.Source = image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Önizleme yoksa tür ikonu görünür.
        }
    }

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hovering = true;
        _hideTimer.Stop();
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hovering = false;
        if (_visible && Progress.Visibility != Visibility.Visible) _hideTimer.Start();
    }

    private void Root_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Düğme yazısı bir ContentPresenter içindedir; Parent'a bakmak yetmez, görsel ağaçta yukarı çıkılır.
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != Root; node = VisualTreeHelper.GetParent(node))
            if (node is Button) return;
        if (_completedInRun == 1 && _lastPath is not null)
        {
            Process.Start(new ProcessStartInfo(_lastPath) { UseShellExecute = true });
        }
        else
        {
            App.Current.Panel.Toggle();
        }
        _hovering = false;
        Dismiss();
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPath is null) return;
        await ClipboardFiles.CopyAsync(_lastPath);
        Subtitle.Text = Loc.T("Copied");
        CopyButton.Visibility = Visibility.Collapsed;
        // Fare hâlâ kutunun üzerinde olsa da "Kopyalandı" bir an görünüp kutu kendiliğinden kapanır.
        await Task.Delay(800);
        _hovering = false;
        Dismiss();
    }
}
