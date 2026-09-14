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
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.App;

public partial class App : Application
{
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private NativeMenuItem? _pauseItem;
    private NativeMenuItem? _statusItem;

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
            _window = new MainWindow { DataContext = _vm };
            _window.Closing += OnWindowClosing;
            desktop.MainWindow = _window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            SetUpTray(desktop);

            if (trayMode)
            {
                _window.ShowInTaskbar = false;
                StartLoop();
            }
            else
            {
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

            _statusItem = new NativeMenuItem("Фоновий режим: вимкнено") { IsEnabled = false };
            var open = new NativeMenuItem("Відкрити вікно");
            open.Click += (_, _) => ShowWindow();
            var syncNow = new NativeMenuItem("Синхронізувати зараз");
            syncNow.Click += (_, _) => SyncNow();
            _pauseItem = new NativeMenuItem("Запустити фоновий режим");
            _pauseItem.Click += (_, _) => ToggleLoop();
            var exit = new NativeMenuItem("Вийти");
            exit.Click += (_, _) => Shutdown(desktop);

            var menu = new NativeMenu();
            menu.Add(_statusItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(open);
            menu.Add(syncNow);
            menu.Add(_pauseItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(exit);

            _tray = new TrayIcon { Icon = icon, ToolTipText = "WorkFlowSync", Menu = menu, IsVisible = true };
            _tray.Clicked += (_, _) => ShowWindow();
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
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
                _vm.StatusText = "Програма згорнулася в область сповіщень. Значок біля годинника: відкрити вікно, синхронізувати, вийти.";
        }
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        Dispatcher.UIThread.Post(() =>
        {
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
        var text = loop is null || loop.State == LoopState.Stopped ? "Фоновий режим: вимкнено" : loop.StatusText;
        if (_statusItem is not null) _statusItem.Header = text;
        if (_tray is not null) _tray.ToolTipText = $"WorkFlowSync — {text}";
        if (_pauseItem is not null)
            _pauseItem.Header = loop is { IsActive: true } ? "Призупинити фоновий режим" : "Запустити фоновий режим";
        _vm?.Run.SyncFromLoop();
    });

    private void Shutdown(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _vm?.Background.Stop();
        _vm?.Background.Dispose();
        if (_tray is not null) _tray.IsVisible = false;
        desktop.Shutdown();
    }
}
