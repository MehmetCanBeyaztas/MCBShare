using System.Diagnostics;
using MCBShare.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.FileProperties;
using WinRT.Interop;

namespace MCBShare.App;

public sealed partial class FileRow(HistoryItem item) : System.ComponentModel.INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;

    public HistoryItem Item { get; } = item;
    public string Name => Item.Name;
    public string Detail => $"{Formats.Size(Item.Size)} · {Formats.Ago(Item.ReceivedAt)} · {Item.DeviceName}";

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Panelin üstündeki canlı aktarım satırı.</summary>
internal sealed class ActiveTransfer
{
    private readonly TextBlock _name = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detail = new() { FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _bar = new() { Maximum = 1, Margin = new Thickness(0, 4, 0, 0) };

    public ActiveTransfer()
    {
        Row = new StackPanel();
        Row.Children.Add(_name);
        Row.Children.Add(_detail);
        Row.Children.Add(_bar);
    }

    public StackPanel Row { get; }
    public string Name { get; private set; } = "";
    public long Received { get; private set; }
    public long? Total { get; private set; }
    public long LastUpdate { get; private set; } = Stopwatch.GetTimestamp();

    public void Update(TransferUpdate update)
    {
        Name = update.FileName;
        Received = update.Received;
        Total = update.Total;
        LastUpdate = Stopwatch.GetTimestamp();

        _name.Text = Name;
        var parts = new List<string>();
        if (update.Total is > 0)
        {
            var progress = Math.Clamp((double)update.Received / update.Total.Value, 0, 1);
            _bar.IsIndeterminate = false;
            _bar.Value = progress;
            parts.Add($"%{(int)(progress * 100)}");
        }
        else
        {
            _bar.IsIndeterminate = true;
            parts.Add(Formats.Size(update.Received));
        }
        if (update.BatchTotal is > 1) parts.Add($"{update.BatchIndex}/{update.BatchTotal}");
        parts.Add(update.DeviceName);
        _detail.Text = string.Join(" · ", parts);
    }

    public void MarkStalled() => _detail.Text = Loc.T("TransferWaiting");
}

public sealed partial class PanelWindow : Window
{
    private const int LogicalWidth = 360, LogicalHeight = 520, ScreenMargin = 12;

    private readonly MCBShareCore _core = App.Current.Core;
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, BitmapImage> _thumbnails = [];
    private long _hiddenAt;
    private string? _activeShare;
    private DateTimeOffset _shareExpiresAt;
    private readonly DispatcherTimer _releaseWatch = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _transferWatch = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<string, ActiveTransfer> _transfers = [];
    private string? _lastSentText;
    private bool _qrFromText;
    private int _rowCount;
    private bool _pinned;
    private bool _dragOverPanel;

    public PanelWindow()
    {
        InitializeComponent();
        Handle = WindowNative.GetWindowHandle(this);

        Native.PrepareBorderless(this, Handle, 0);

        OpenFolderButton.Content = Loc.T("OpenFolder");
        ConnectButton.Content = Loc.T("ConnectPhone");
        TrustButton.Content = Loc.T("TrustNetwork");
        EmptyText.Text = Loc.T("Empty");

        _countdown.Tick += (_, _) => UpdateCountdown();
        _releaseWatch.Tick += (_, _) => OnReleaseWatchTick();
        _transferWatch.Tick += (_, _) => OnTransferWatchTick();
        TextTitle.Text = Loc.T("TextTitle");
        TextBack.Content = Loc.T("Back");
        TextInput.PlaceholderText = Loc.T("TextPlaceholder");
        TextQrButton.Content = Loc.T("ShowTextQr");
        TextSendButton.Content = Loc.T("SendToPhone");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TextButton, Loc.T("SendText"));
        ToolTipService.SetToolTip(TextButton, Loc.T("SendText"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SettingsButton, Loc.T("Settings"));
        ToolTipService.SetToolTip(SettingsButton, Loc.T("Settings"));
        Root.DragLeave += (_, _) => _dragOverPanel = false;
        Activated += OnActivated;
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            HidePanel();
        };
    }

    public nint Handle { get; }

    public bool IsOpen => AppWindow.IsVisible;

    /// <summary>Panel kapalı, aktarım ve paylaşım yoksa güncelleme yeniden başlatması kimseyi kesmez.</summary>
    public bool IsIdle => !IsOpen && _transfers.Count == 0 && _activeShare is null;

    /// <summary>Aktarım listesi değişince tepsi ikonu bunu okur.</summary>
    public event Action? TransfersChanged;

    /// <summary>Süren aktarımların toplam ilerlemesi ve ipucu metni. Aktarım yoksa null.</summary>
    public (double? Progress, string Text)? TransferSummary
    {
        get
        {
            if (_transfers.Count == 0) return null;
            var sized = _transfers.Values.Where(t => t.Total is > 0).ToList();
            double? progress = sized.Count == 0
                ? null
                : Math.Clamp((double)sized.Sum(t => t.Received) / sized.Sum(t => t.Total!.Value), 0, 1);
            var name = _transfers.Count == 1 ? _transfers.Values.First().Name : string.Format(Loc.T("FilesCount"), _transfers.Count);
            if (name.Length > 60) name = name[..60] + "…";
            return (progress, progress is { } p ? $"{name} %{(int)(p * 100)}" : name);
        }
    }

    public void OnTransfer(TransferUpdate update)
    {
        if (update.State != TransferState.Running)
        {
            if (_transfers.Remove(update.TransferId, out var finished))
                TransferRows.Children.Remove(finished.Row);
        }
        else
        {
            if (!_transfers.TryGetValue(update.TransferId, out var transfer))
            {
                transfer = new ActiveTransfer();
                _transfers[update.TransferId] = transfer;
                TransferRows.Children.Insert(0, transfer.Row);
                _transferWatch.Start();
            }
            transfer.Update(update);
        }
        if (_transfers.Count == 0) _transferWatch.Stop();
        TransferRows.Visibility = _transfers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyText();
        TransfersChanged?.Invoke();
    }

    public void OnTextDelivered(string deviceName)
    {
        if (_lastSentText is not null)
            TextStatus.Text = string.Format(Loc.T("TextDelivered"), deviceName);
    }

    /// <summary>Telefon parçalar arasında susarsa sunucu bir şey görmez; satır sonsuza dek kalmamalı.</summary>
    private void OnTransferWatchTick()
    {
        foreach (var (id, transfer) in _transfers.ToList())
        {
            var silent = Stopwatch.GetElapsedTime(transfer.LastUpdate);
            if (silent >= TimeSpan.FromMinutes(1))
            {
                _transfers.Remove(id);
                TransferRows.Children.Remove(transfer.Row);
            }
            else if (silent >= TimeSpan.FromSeconds(15))
            {
                transfer.MarkStalled();
            }
        }
        if (_transfers.Count == 0) _transferWatch.Stop();
        TransferRows.Visibility = _transfers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyText();
        TransfersChanged?.Invoke();
    }

    private void UpdateEmptyText() =>
        EmptyText.Visibility = _rowCount == 0 && _transfers.Count == 0 && FileList.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;

    public void Toggle()
    {
        if (IsOpen)
        {
            HidePanel();
            return;
        }
        // Tepsiye tıklayınca önce panel odağını kaybedip kapanır; aynı tık yeniden açmamalı.
        if (Stopwatch.GetElapsedTime(_hiddenAt) < TimeSpan.FromMilliseconds(250)) return;
        ShowList();
        ShowPanel();
    }

    public void RefreshStatus()
    {
        var network = _core.CurrentNetworkName;
        var status = Loc.StateText(_core.State);
        StatusText.Text = network is null ? Loc.T("NoNetwork")
            : _core.State == ServerState.Running ? $"{status} · {_core.BaseUrl}"
            : $"{status} · {network}";
        TrustButton.Visibility = network is not null && !_core.IsCurrentNetworkTrusted ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.IsEnabled = _core.State == ServerState.Running;
    }

    public void RefreshList()
    {
        if (!IsOpen) return;
        var rows = _core.History.Recent().Select(i => new FileRow(i)).ToList();
        FileList.ItemsSource = rows;
        _rowCount = rows.Count;
        UpdateEmptyText();
        foreach (var row in rows) _ = LoadThumbnailAsync(row);
    }

    public async void ShowShare(ShareSnapshot share)
    {
        _activeShare = share.Code;
        _qrFromText = false;
        _pinned = true;
        _shareExpiresAt = share.ExpiresAt;
        QrTitle.Text = share.Files.Count == 1 ? share.Files[0].Name : string.Format(Loc.T("FilesCount"), share.Files.Count);
        QrButton.Content = Loc.T("Cancel");
        ShowQrView();
        UpdateShareStatus(share);
        _countdown.Start();
        UpdateCountdown();
        ShowPanel();
        if (_core.ShareUrl(share.Code) is { } url)
            QrImage.Source = await QrImages.CreateAsync(url);
    }

    public async void OnShareChanged(ShareSnapshot share)
    {
        if (share.Code != _activeShare) return;
        _shareExpiresAt = share.ExpiresAt;
        UpdateShareStatus(share);
        if (share.State is ShareState.Cancelled)
        {
            EndShareView();
        }
        else if (share.State is ShareState.Downloaded or ShareState.Expired)
        {
            _countdown.Stop();
            QrCountdown.Text = "";
            await Task.Delay(4000);
            if (_activeShare == share.Code)
            {
                EndShareView();
                HidePanel();
            }
        }
    }

    private void UpdateShareStatus(ShareSnapshot share)
    {
        QrProgress.Visibility = share.State == ShareState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        QrProgress.Value = share.Progress;
        QrStatus.Text = share.State switch
        {
            ShareState.WaitingForPhone => Loc.T("WaitingPhone"),
            ShareState.Downloading => string.Format(Loc.T("Downloading"), (int)(share.Progress * 100)),
            ShareState.Downloaded => Loc.T("Downloaded"),
            ShareState.Expired => Loc.T("Expired"),
            _ => "",
        };
    }

    private void UpdateCountdown()
    {
        var left = _shareExpiresAt - DateTimeOffset.UtcNow;
        QrCountdown.Text = left > TimeSpan.Zero ? string.Format(Loc.T("Remaining"), left.ToString(@"m\:ss")) : "";
    }

    private void EndShareView()
    {
        _activeShare = null;
        _pinned = false;
        _countdown.Stop();
        ShowList();
    }

    private void ShowList()
    {
        if (_activeShare is not null) return;
        QrView.Visibility = Visibility.Collapsed;
        TextView.Visibility = Visibility.Collapsed;
        FileList.Visibility = Visibility.Visible;
        UpdateEmptyText();
    }

    private void ShowTextView()
    {
        FileList.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        QrView.Visibility = Visibility.Collapsed;
        TextView.Visibility = Visibility.Visible;
        UpdateTextButtons();
        TextInput.Focus(FocusState.Programmatic);
    }

    private void UpdateTextButtons()
    {
        var hasText = TextInput.Text.Length > 0;
        TextQrButton.IsEnabled = hasText;
        TextSendButton.IsEnabled = hasText && _core.State == ServerState.Running;
    }

    private async void TextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeShare is not null) return;
        _lastSentText = null;
        TextStatus.Text = "";
        if (TextInput.Text.Length == 0)
        {
            TextInput.Text = await ClipboardFiles.ReadTextAsync() ?? "";
            TextInput.SelectAll();
        }
        ShowTextView();
    }

    private void TextBack_Click(object sender, RoutedEventArgs e) => ShowList();

    private void TextInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TextInput.Text != _lastSentText) TextStatus.Text = "";
        UpdateTextButtons();
    }

    private void TextSend_Click(object sender, RoutedEventArgs e)
    {
        _lastSentText = TextInput.Text;
        _core.Clipboard.SendToPhone(_lastSentText);
        TextStatus.Text = Loc.T("TextWaiting");
    }

    private async void TextQr_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Metin QR'ın kendisindedir; telefonun bağlı ya da aynı ağda olması gerekmez.
            var image = await QrImages.CreateAsync(TextInput.Text);
            _qrFromText = true;
            QrTitle.Text = Loc.T("TextTitle");
            QrStatus.Text = Loc.T("TextQrHint");
            QrButton.Content = Loc.T("Back");
            ShowQrView();
            QrImage.Source = image;
        }
        catch (QRCoder.Exceptions.DataTooLongException)
        {
            TextStatus.Text = Loc.T("TextTooLong");
        }
    }

    private void ShowQrView()
    {
        FileList.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        TextView.Visibility = Visibility.Collapsed;
        QrView.Visibility = Visibility.Visible;
        QrCountdown.Text = "";
        QrProgress.Visibility = Visibility.Collapsed;
    }

    private void ShowPanel()
    {
        var wasOpen = IsOpen;
        PositionWindow();
        RefreshStatus();
        AppWindow.Show();
        Activate();
        Native.SetForegroundWindow(Handle);
        RefreshList();
        if (!wasOpen) SlideIn.Begin();
    }

    private void HidePanel()
    {
        AppWindow.Hide();
        _hiddenAt = Stopwatch.GetTimestamp();
        FileList.ItemsSource = null;
        _thumbnails.Clear();
    }

    private void PositionWindow()
    {
        Native.GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary).WorkArea;
        var scale = Native.GetDpiForWindow(Handle) / 96.0;
        var width = (int)(LogicalWidth * scale);
        var height = Math.Min((int)(LogicalHeight * scale), area.Height - (int)(2 * ScreenMargin * scale));
        var margin = (int)(ScreenMargin * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - width - margin, area.Y + area.Height - height - margin, width, height));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _releaseWatch.Stop();
            return;
        }
        // QR okutulurken kullanıcı başka yere tıklasa da panel açık kalır.
        if (_pinned || Native.DevMode || !IsOpen) return;

        // Gezgin'de dosyaya basmak da odağı alır; bu bir sürüklemenin başlangıcı olabilir.
        if (Native.IsMouseButtonDown()) _releaseWatch.Start();
        else HidePanel();
    }

    private void OnReleaseWatchTick()
    {
        if (Native.IsMouseButtonDown()) return;
        _releaseWatch.Stop();
        // Üzerine bırakıldıysa kararı Root_Drop verir.
        if (!_dragOverPanel && !_pinned && IsOpen) HidePanel();
    }

    private async Task LoadThumbnailAsync(FileRow row)
    {
        try
        {
            if (!_thumbnails.TryGetValue(row.Item.Path, out var image))
            {
                var file = await StorageFile.GetFileFromPathAsync(row.Item.Path);
                using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 64, ThumbnailOptions.ResizeThumbnail);
                image = new BitmapImage();
                await image.SetSourceAsync(thumb);
                _thumbnails[row.Item.Path] = image;
            }
            row.Thumbnail = image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Dosya silinmiş olabilir ya da önizleme üretilemiyordur; satır önizlemesiz kalır.
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        HidePanel();
        App.Current.OpenSettings();
    }

    private async void Trust_Click(object sender, RoutedEventArgs e)
    {
        await _core.TrustCurrentNetworkAsync();
        RefreshStatus();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_core.Settings.ReceiveFolder);
        Process.Start(new ProcessStartInfo(_core.Settings.ReceiveFolder) { UseShellExecute = true });
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_core.PairingUrl is not { } url) return;
        _qrFromText = false;
        QrTitle.Text = Loc.T("ConnectPhone");
        QrStatus.Text = Loc.T("PairHint");
        QrButton.Content = Loc.T("Back");
        ShowQrView();
        QrImage.Source = await QrImages.CreateAsync(url);
    }

    private void QrButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeShare is { } code)
        {
            _core.Shares.Cancel(code);
            return;
        }
        if (_qrFromText)
        {
            ShowTextView();
            return;
        }
        ShowList();
        RefreshList();
    }

    private void FileList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FileRow row) return;
        OpenFile(row.Item.Path);
    }

    private void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            HidePanel();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            _core.Log.Error("File could not be opened", ex);
        }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.Tag is not FileRow row) return;
        await ClipboardFiles.CopyAsync(row.Item.Path);
        var original = button.Content;
        button.Content = new TextBlock { Text = Loc.T("Copied"), FontSize = 11 };
        await Task.Delay(1500);
        button.Content = original;
    }

    private void Row_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileRow row } element) return;
        var show = new MenuFlyoutItem { Text = Loc.T("ShowInFolder") };
        show.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{row.Item.Path}\"");
        var delete = new MenuFlyoutItem { Text = Loc.T("Delete") };
        delete.Click += (_, _) => _core.History.Remove(row.Item.Id, deleteFile: true);
        var menu = new MenuFlyout();
        menu.Items.Add(show);
        menu.Items.Add(delete);
        menu.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private void FileList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var rows = e.Items.OfType<FileRow>().ToList();
        if (rows.Count == 0) return;
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var files = new List<IStorageItem>();
                foreach (var row in rows) files.Add(await StorageFile.GetFileFromPathAsync(row.Item.Path));
                request.SetData(files);
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        _dragOverPanel = true;
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        _dragOverPanel = false;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        App.Current.ShareFiles(items.OfType<StorageFile>().Select(f => f.Path));
        // Yalnızca klasör bırakıldıysa paylaşım başlamaz; odaksız panel açık kalmamalı.
        if (!_pinned) HidePanel();
    }
}
