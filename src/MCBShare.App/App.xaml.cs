using System.IO.Pipes;
using H.NotifyIcon;
using MCBShare.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace MCBShare.App;

public partial class App : Application
{
    private const string InstanceName = "MCBShare.SingleInstance";

    private Mutex? _instance;
    private TaskbarIcon? _tray;
    private SettingsWindow? _settings;
    private ToastWindow? _toast;
    private WindowsNetworkIdentity? _network;
    private HotkeyHost? _hotkeys;
    private (bool, int)? _trayIconState;
    private DispatcherQueueTimer? _idleWatch;
    private volatile bool _isIdle;

    public App() => InitializeComponent();

    public static new App Current => (App)Application.Current;

    public MCBShareCore Core { get; private set; } = null!;
    public PanelWindow Panel { get; private set; } = null!;
    public DispatcherQueue Ui { get; private set; } = null!;
    public AppUpdater Updater { get; private set; } = null!;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        _instance = new Mutex(true, InstanceName, out var isFirst);
        if (!isFirst)
        {
            await ForwardToRunningInstanceAsync(commandLine);
            Exit();
            return;
        }

        Ui = DispatcherQueue.GetForCurrentThread();
        var dataFolder = Environment.GetEnvironmentVariable("MCBSHARE_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCBShare");
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        _network = new WindowsNetworkIdentity();
        Core = new MCBShareCore(new CoreOptions
        {
            DataFolder = dataFolder,
            DefaultReceiveFolder = Path.Combine(pictures, "Downloads", "MCBShare"),
            Network = _network,
            BindAddress = System.Net.IPAddress.TryParse(Environment.GetEnvironmentVariable("MCBSHARE_BIND"), out var bind) ? bind : System.Net.IPAddress.Any,
            Processor = new HeicProcessor(() => Core!.Settings),
        });
        UnhandledException += (_, e) => Core.Log.Error("Unhandled UI error: " + e.Message, e.Exception);
        Directory.CreateDirectory(Core.Settings.ReceiveFolder);
        Loc.Apply(Core.Settings.Language);

        Panel = new PanelWindow();
        _toast = new ToastWindow();
        CreateTray();

        Core.StateChanged += () => Ui.TryEnqueue(UpdateTray);
        Panel.TransfersChanged += UpdateTray;
        Core.Uploads.TransferChanged += update => Ui.TryEnqueue(() =>
        {
            Panel.OnTransfer(update);
            _toast.OnTransfer(update);
        });
        Core.Clipboard.TextDelivered += device => Ui.TryEnqueue(() => Panel.OnTextDelivered(device));
        Core.Devices.Changed += () => Ui.TryEnqueue(Panel.RefreshTextTargets);
        Core.History.Changed += () => Ui.TryEnqueue(Panel.RefreshList);
        Core.Shares.ShareChanged += share => Ui.TryEnqueue(() => Panel.OnShareChanged(share));
        Core.Clipboard.TextReceived += (_, text) => Ui.TryEnqueue(() => OnPhoneText(text));

        _hotkeys = new HotkeyHost(Panel.Handle);
        _hotkeys.Pressed += OnHotkey;

        Updater = new AppUpdater(Core.Log, () => _isIdle, Quit);
        _idleWatch = Ui.CreateTimer();
        _idleWatch.Interval = TimeSpan.FromSeconds(20);
        _idleWatch.Tick += (_, _) => _isIdle = _settings is null && Panel.IsIdle;
        _idleWatch.Start();
        _ = Updater.RunAsync();

        ListenForOtherInstances();
        Core.RunCleanup();
        await Core.EvaluateAsync();
        UpdateTray();
        HandleCommandLine(commandLine);
    }

    public void ShareFiles(IEnumerable<string> paths)
    {
        try
        {
            var share = Core.Shares.Create(paths);
            Panel.ShowShare(share);
        }
        catch (ArgumentException)
        {
            // Seçilen öğelerde dosya yoksa (ör. yalnızca klasör) gösterilecek bir şey de yok.
        }
    }

    public void OpenSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.Activate();
    }

    public async void Quit()
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        await Core.DisposeAsync();
        _instance?.ReleaseMutex();
        Environment.Exit(0);
    }

    private void CreateTray()
    {
        var menu = new MenuFlyout();
        var settings = new MenuFlyoutItem { Text = Loc.T("Settings") };
        settings.Command = new Relay(OpenSettings);
        var exit = new MenuFlyoutItem { Text = Loc.T("Exit") };
        exit.Command = new Relay(Quit);
        menu.Items.Add(settings);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);

        _tray = new TaskbarIcon
        {
            ToolTipText = "MCBShare",
            LeftClickCommand = new Relay(Panel.Toggle),
            ContextFlyout = menu,
            ContextMenuMode = H.NotifyIcon.ContextMenuMode.PopupMenu,
            Icon = TrayIcons.Create(active: false),
        };
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    private void UpdateTray()
    {
        if (_tray is null) return;
        var summary = Panel.TransferSummary;
        var running = Core.State == ServerState.Running;
        // İkon yalnızca çubukta görünür bir fark oluşunca yeniden çizilir.
        var iconState = (running, summary?.Progress is { } p ? (int)(p * 32) : summary is null ? -1 : -2);
        if (iconState != _trayIconState)
        {
            _trayIconState = iconState;
            var previous = _tray.Icon;
            _tray.Icon = TrayIcons.Create(running, summary?.Progress);
            previous?.Dispose();
        }
        _tray.ToolTipText = "MCBShare · " + (summary?.Text ?? Loc.StateText(Core.State));
        Panel.RefreshStatus();
    }

    private void OnHotkey(int id)
    {
        if (id == HotkeyHost.PanelHotkey)
        {
            Panel.Toggle();
        }
        else if (id == HotkeyHost.SendImageHotkey)
        {
            _ = SendImageAsync();
        }
    }

    private async Task SendImageAsync()
    {
        var path = await ClipboardFiles.SaveClipboardImageAsync(Core.OutboxFolder) ?? ClipboardFiles.LatestScreenshot();
        if (path is not null) ShareFiles([path]);
    }

    private void OnPhoneText(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        _toast?.ShowMessage(Loc.T("ClipboardReceived"));
    }

    private void HandleCommandLine(string[] args)
    {
        var shareIndex = Array.IndexOf(args, "--share");
        if (shareIndex >= 0)
            ShareFiles(args.Skip(shareIndex + 1));
        else if (args.Contains("--show-panel"))
            Panel.Toggle();
    }

    private static async Task ForwardToRunningInstanceAsync(string[] args)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", InstanceName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(2000);
            await using var writer = new StreamWriter(pipe);
            foreach (var arg in args.DefaultIfEmpty("--show-panel"))
                await writer.WriteLineAsync(arg);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            // İlk kopya kapanıyor olabilir; yapılacak bir şey yok.
        }
    }

    private void ListenForOtherInstances()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(InstanceName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync();
                    using var reader = new StreamReader(pipe);
                    var lines = new List<string>();
                    while (await reader.ReadLineAsync() is { } line) lines.Add(line);
                    var received = lines.ToArray();
                    Ui.TryEnqueue(() => HandleCommandLine(received));
                }
                catch (IOException ex)
                {
                    Core.Log.Error("Instance pipe failed", ex);
                    await Task.Delay(1000);
                }
            }
        });
    }
}
