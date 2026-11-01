using System.Diagnostics;
using MCBShare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MCBShare.App;

public sealed partial class SettingsWindow : Window
{
    private readonly MCBShareCore _core = App.Current.Core;
    private bool _loading = true;
    private string? _shortcutKey;

    public SettingsWindow()
    {
        InitializeComponent();
        Title = "MCBShare · " + Loc.T("Settings");
        AppWindow.Resize(new SizeInt32(720, 820));

        GeneralTitle.Text = Loc.T("General");
        FolderLabel.Text = Loc.T("ReceiveFolder");
        ChangeFolder.Content = Loc.T("Change");
        StartToggle.Header = Loc.T("StartWithWindows");
        ToastToggle.Header = Loc.T("ShowToast");
        HeicToggle.Header = Loc.T("ConvertHeic");
        DateToggle.Header = Loc.T("ByDate");
        DupToggle.Header = Loc.T("SkipDuplicates");
        RetentionBox.Header = Loc.T("Retention");
        LanguageBox.Header = Loc.T("Language");
        NetworksTitle.Text = Loc.T("Networks");
        TrustButton.Content = Loc.T("TrustNetwork");
        DevicesTitle.Text = Loc.T("Devices");
        RenewPair.Content = Loc.T("NewPairCode");
        ShortcutTitle.Text = Loc.T("Shortcut");
        ShortcutSteps.Text = Loc.T("ShortcutSteps");
        ShortcutCreate.Content = Loc.T("ShortcutCreate");
        LogsLink.Content = Loc.T("Logs");
        AboutTitle.Text = Loc.T("About");
        VersionText.Text = string.Format(Loc.T("Version"), AppUpdater.CurrentVersion);
        UpdateButton.Content = Loc.T("CheckUpdates");

        var s = _core.Settings;
        FolderBox.Text = s.ReceiveFolder;
        StartToggle.IsOn = StartupRegistration.IsEnabled;
        ToastToggle.IsOn = s.ShowTransferNotifications;
        HeicToggle.IsOn = s.ConvertHeicToJpg;
        DateToggle.IsOn = s.OrganizeByDate;
        DupToggle.IsOn = s.SkipDuplicates;
        RetentionBox.Value = s.RetentionDays;
        LanguageBox.Items.Add(Loc.T("SystemLanguage"));
        LanguageBox.Items.Add("Türkçe");
        LanguageBox.Items.Add("English");
        LanguageBox.SelectedIndex = s.Language switch { "tr" => 1, "en" => 2, _ => 0 };

        RefreshNetworks();
        RefreshDevices();
        _core.Devices.Changed += OnDevicesChanged;
        Closed += (_, _) => _core.Devices.Changed -= OnDevicesChanged;
        _loading = false;
    }

    private void OnDevicesChanged() => DispatcherQueue.TryEnqueue(RefreshDevices);

    private void RefreshNetworks()
    {
        NetworkList.Children.Clear();
        foreach (var name in _core.Settings.TrustedNetworks)
            NetworkList.Children.Add(Row(name, async () =>
            {
                await _core.UntrustNetworkAsync(name);
                RefreshNetworks();
            }));
        TrustButton.Visibility = _core.CurrentNetworkName is not null && !_core.IsCurrentNetworkTrusted
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshDevices()
    {
        DeviceList.Children.Clear();
        if (_core.Devices.Devices.Count == 0)
            DeviceList.Children.Add(new TextBlock { Text = Loc.T("NoDevices"), Opacity = 0.7 });
        foreach (var device in _core.Devices.Devices)
            DeviceList.Children.Add(Row(device.Name, () =>
            {
                _core.Devices.Remove(device.Id);
                return Task.CompletedTask;
            }));
    }

    private static Grid Row(string text, Func<Task> onRemove)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var remove = new Button { Content = Loc.T("Remove") };
        remove.Click += async (_, _) => await onRemove();
        Grid.SetColumn(remove, 1);
        grid.Children.Add(remove);
        return grid;
    }

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        if (await picker.PickSingleFolderAsync() is not { } folder) return;
        _core.Settings.ReceiveFolder = folder.Path;
        _core.SaveSettings();
        FolderBox.Text = folder.Path;
    }

    private void Start_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) StartupRegistration.IsEnabled = StartToggle.IsOn;
    }

    private void Option_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _core.Settings;
        s.ShowTransferNotifications = ToastToggle.IsOn;
        s.ConvertHeicToJpg = HeicToggle.IsOn;
        s.OrganizeByDate = DateToggle.IsOn;
        s.SkipDuplicates = DupToggle.IsOn;
        _core.SaveSettings();
    }

    private void Retention_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        _core.Settings.RetentionDays = (int)args.NewValue;
        _core.SaveSettings();
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _core.Settings.Language = LanguageBox.SelectedIndex switch { 1 => "tr", 2 => "en", _ => "" };
        _core.SaveSettings();
    }

    private async void Trust_Click(object sender, RoutedEventArgs e)
    {
        await _core.TrustCurrentNetworkAsync();
        RefreshNetworks();
    }

    private void RenewPair_Click(object sender, RoutedEventArgs e) => _core.RegeneratePairingToken();

    private async void ShortcutCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_core.BaseUrl is null)
        {
            ShortcutSteps.Text = Loc.T("ShortcutNeedsServer");
            return;
        }
        // Aynı pencerede yeniden basmak yeni bir cihaz kaydı biriktirmesin.
        _shortcutKey ??= _core.Devices.Register(Loc.T("ShortcutName"), isShortcut: true).Key;
        if (_core.ShortcutSetupUrl(_shortcutKey) is not { } url) return;
        ShortcutSteps.Text = Loc.T("ShortcutSteps");
        ShortcutQr.Source = await QrImages.CreateAsync(url);
        ShortcutQrFrame.Visibility = Visibility.Visible;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateStatus.Text = Loc.T("UpdateChecking");
        UpdateStatus.Text = await App.Current.Updater.CheckAndDownloadAsync();
        UpdateButton.IsEnabled = true;
    }

    private void Logs_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_core.Log.Folder) { UseShellExecute = true });
}
