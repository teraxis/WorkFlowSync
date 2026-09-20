using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using WorkFlowSync.App.Services;
using WorkFlowSync.App.ViewModels;
using WorkFlowSync.App.Views;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.I18n;
using WorkFlowSync.Core.Notifications;

namespace WorkFlowSync.App;

public partial class App : Application
{
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private NativeMenuItem? _pauseItem;
    private NativeMenuItem? _statusItem;
    private NativeMenuItem? _recentItem;
    private NativeMenuItem? _openItem;
    private NativeMenuItem? _syncNowItem;
    private NativeMenuItem? _exitItem;
    private RecentChangesWindow? _recentWindow;
    private RecentChangesViewModel? _recentModel;
    private NotificationWindow? _notification;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();
            var configPath = ConfigFile.DefaultPath;
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--config") configPath = args[i + 1];
            // --tray: start hidden in the notification area and run the loop in the background.
            var trayMode = args.Contains("--tray") || args.Contains("--minimized");
            // --page <0|1|2>: open on a given page (used when documenting or debugging the UI).
            var page = 0;
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--page" && int.TryParse(args[i + 1], out var n)) page = Math.Clamp(n, 0, 2);

            _vm = new MainViewModel(configPath);
            _vm.ThemeApplied += ApplyTheme;
            ApplyTheme(_vm.Theme);
            _vm.Background.Changed += UpdateTray;
            _vm.Background.PassCompleted += OnPassCompleted;
            _window = new MainWindow { DataContext = _vm };
            _window.Closing += OnWindowClosing;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            SetUpTray(desktop);

            // Launching the program again (Start menu, shortcut, double-click) must not open a second window:
            // the other process exits straight away and asks this one to come forward. ShowWindow already
            // marshals to the UI thread, so the background signal can call it directly.
            SingleInstance.Current?.ListenForOtherLaunches(ShowWindow);

            if (trayMode)
            {
                // Autostart must be silent: the window is never shown, so MainWindow stays unset —
                // the desktop lifetime shows whatever is assigned to it once startup finishes.
                _window.ShowInTaskbar = false;
                StartLoop();
            }
            else
            {
                desktop.MainWindow = _window;
                _window.Show();
            }

            // After the window exists: the nav ListBox writes its own SelectedIndex into the binding while it
            // initialises, so setting the page earlier would be overwritten.
            if (page != 0) Dispatcher.UIThread.Post(() => _vm!.SelectedPage = page);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Light / dark / follow Windows (docs/product/features/design-system.md).</summary>
    private void ApplyTheme(AppTheme theme) => RequestedThemeVariant = theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    // ------------------------------------------------------------------ tray

    private void SetUpTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://WorkFlowSync/Assets/app.ico"));
            var icon = new WindowIcon(stream);
            _window!.Icon = icon;

            _statusItem = new NativeMenuItem(I18n.T("tray.status_off")) { IsEnabled = false };
            _recentItem = new NativeMenuItem(I18n.T("tray.recent"));
            _recentItem.Click += (_, _) => ShowRecentChanges();
            _openItem = new NativeMenuItem(I18n.T("tray.open"));
            _openItem.Click += (_, _) => ShowWindow();
            _syncNowItem = new NativeMenuItem(I18n.T("tray.sync_now"));
            _syncNowItem.Click += (_, _) => SyncNow();
            _pauseItem = new NativeMenuItem(I18n.T("tray.resume"));
            _pauseItem.Click += (_, _) => ToggleLoop();
            _exitItem = new NativeMenuItem(I18n.T("tray.exit"));
            _exitItem.Click += (_, _) => Shutdown(desktop);

            var menu = new NativeMenu();
            menu.Add(_statusItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(_recentItem);
            menu.Add(_openItem);
            menu.Add(_syncNowItem);
            menu.Add(_pauseItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(_exitItem);

            _tray = new TrayIcon { Icon = icon, ToolTipText = "WorkFlowSync", Menu = menu, IsVisible = true };
            // Left click shows what has arrived lately — the question people open a sync tool to answer.
            // The full window stays one menu item (or one more click) away.
            _tray.Clicked += (_, _) => ShowRecentChanges();
            TrayIcon.SetIcons(this, new TrayIcons { _tray });

            I18n.Instance.LanguageChanged += RefreshTrayText;
        }
        catch (Exception ex)
        {
            // No tray (rare): keep the window-only experience rather than failing to start.
            Console.Error.WriteLine($"tray unavailable: {ex.Message}");
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // The X button hides to the tray; «Вийти» in the tray menu really exits.
        if (_tray is not null && _tray.IsVisible)
        {
            e.Cancel = true;
            _window!.Hide();
            _window.ShowInTaskbar = false;
            if (_vm is not null)
                _vm.StatusText = I18n.T("tray.minimized_hint");
        }
    }

    /// <summary>
    /// Decides whether a finished pass is worth a word, and says it. The rules live in
    /// <see cref="NotificationPolicy"/> so they can be tested; this only puts them on screen.
    /// </summary>
    private void OnPassCompleted(PassResult result)
    {
        if (_vm is null) return;

        var added = result.Pairs.Sum(p => p.Execution?.FilesCopied ?? 0);
        var heldBack = result.Pairs.Sum(p => p.HeldBackRemovals);
        var note = NotificationPolicy.ForPass(_vm.ToConfig(), added, result.Errors, heldBack, DateTimeOffset.Now);
        if (note is null) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_notification is null)
            {
                _notification = new NotificationWindow();
                _notification.Clicked += ShowRecentChanges;
            }
            _notification.Show(note);
        });
    }

    /// <summary>
    /// The list of what arrived lately, in the corner by the clock. Created once and reused: it is opened
    /// often and briefly, and rebuilding a window each time would flicker.
    /// </summary>
    private void ShowRecentChanges()
    {
        if (_vm is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            _recentModel ??= new RecentChangesViewModel(
                () => _vm!.StateFullPath,
                () => _vm!.Pairs.ToDictionary(p => p.Name, p => p.Target, StringComparer.OrdinalIgnoreCase),
                () => _vm!.RecentLimit);

            if (_recentWindow is null)
            {
                _recentWindow = new RecentChangesWindow();
                _recentWindow.MainWindowRequested += ShowWindow;
                _recentWindow.ApprovalWindowRequested += ShowApprovalWindow;
            }
            _recentWindow.ShowNearTray(_recentModel);
        });
    }

    private ApprovalWindow? _approvalWindow;
    private ApprovalViewModel? _approvalModel;

    public void ShowApprovalWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is null) return;
            _approvalModel ??= new ApprovalViewModel(
                () => _vm.StateFullPath,
                () => _vm.Pairs.ToDictionary(p => p.Name, p => (p.Source, p.Target), StringComparer.OrdinalIgnoreCase),
                msg => _vm?.Run.AppendLog(msg));

            if (_approvalWindow is null)
            {
                _approvalWindow = new ApprovalWindow();
                _approvalWindow.Closed += (_, _) => _approvalWindow = null;
            }
            _approvalWindow.DataContext = _approvalModel;
            _approvalModel.Load();
            _approvalWindow.Show();
            _approvalWindow.Activate();
        });
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            // First time out of the tray: the lifetime still has no main window.
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d && d.MainWindow is null)
                d.MainWindow = _window;
            _window.ShowInTaskbar = true;
            _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            _vm?.Run.RefreshSummary();
        });
    }

    private void SyncNow()
    {
        if (_vm is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            ShowWindow();
            if (_vm.Run.RunNowCommand.CanExecute(null)) _vm.Run.RunNowCommand.Execute(null);
        });
    }

    private void ToggleLoop()
    {
        var loop = _vm!.Background;
        if (loop.IsActive) loop.Pause();
        else if (loop.State == LoopState.Paused) loop.Resume();
        else loop.Start();
        _vm.Run.SyncFromLoop();
        UpdateTray();
    }

    private void StartLoop()
    {
        _vm!.Background.Start();
        _vm.Run.SyncFromLoop();
        UpdateTray();
    }

    private void UpdateTray() => Dispatcher.UIThread.Post(() =>
    {
        var loop = _vm?.Background;
        var text = loop is null || loop.State == LoopState.Stopped ? I18n.T("tray.status_off") : loop.StatusText;
        if (_statusItem is not null) _statusItem.Header = text;
        if (_tray is not null) _tray.ToolTipText = $"WorkFlowSync — {text}";
        if (_pauseItem is not null)
            _pauseItem.Header = loop is { IsActive: true } ? I18n.T("tray.pause") : I18n.T("tray.resume");
        _vm?.Run.SyncFromLoop();
    });

    private void RefreshTrayText() => Dispatcher.UIThread.Post(() =>
    {
        UpdateTray();
        if (_recentItem is not null) _recentItem.Header = I18n.T("tray.recent");
        if (_openItem is not null) _openItem.Header = I18n.T("tray.open");
        if (_syncNowItem is not null) _syncNowItem.Header = I18n.T("tray.sync_now");
        if (_exitItem is not null) _exitItem.Header = I18n.T("tray.exit");
    });

    private void Shutdown(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _vm?.Background.Stop();
        _vm?.Background.Dispose();
        if (_tray is not null) _tray.IsVisible = false;
        desktop.Shutdown();
    }
}
