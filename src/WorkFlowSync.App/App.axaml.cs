using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
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
    private BackgroundLoop? _loop;
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

            _vm = new MainViewModel(configPath);
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
        }

        base.OnFrameworkInitializationCompleted();
    }

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
        if (_loop is { IsActive: true }) _loop.Pause();
        else if (_loop is { State: LoopState.Paused }) _loop.Resume();
        else StartLoop();
        UpdateTray();
    }

    private void StartLoop()
    {
        _loop ??= new BackgroundLoop(_vm!.ConfigPath, () =>
        {
            var cfg = _vm!.ToConfig();
            return Path.IsPathRooted(cfg.LogPath) ? cfg.LogPath : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_vm.ConfigPath))!, cfg.LogPath);
        });
        _loop.Changed += UpdateTray;
        _loop.Start();
        UpdateTray();
    }

    private void UpdateTray() => Dispatcher.UIThread.Post(() =>
    {
        var text = _loop?.StatusText ?? "Фоновий режим: вимкнено";
        if (_statusItem is not null) _statusItem.Header = text;
        if (_tray is not null) _tray.ToolTipText = $"WorkFlowSync — {text}";
        if (_pauseItem is not null)
            _pauseItem.Header = _loop is { IsActive: true } ? "Призупинити фоновий режим" : "Запустити фоновий режим";
        if (_vm is not null) _vm.BackgroundStatus = text;
    });

    private void Shutdown(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _loop?.Stop();
        _loop?.Dispose();
        if (_tray is not null) _tray.IsVisible = false;
        desktop.Shutdown();
    }
}
