using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using WorkFlowSync.Core.I18n;
using WorkFlowSync.Core.Update;

namespace WorkFlowSync.App.Views;

/// <summary>
/// A notification window near the system tray that offers three choices when an update is available:
/// update now, remind later, or skip this version.  When the user clicks «Update», the buttons
/// are replaced by a progress bar and the download/install sequence runs in place.
/// </summary>
public partial class UpdateNotificationWindow : Window
{
    private UpdateInfo? _info;
    private CancellationTokenSource? _downloadCts;

    /// <summary>Raised when the user clicks «Update» and download completes. The caller should exit the app.</summary>
    public event Action<UpdateInfo>? UpdateRequested;

    /// <summary>Raised when the user clicks «Remind later» — the window hides, next check will show it again.</summary>
    public event Action? LaterRequested;

    /// <summary>Raised when the user clicks «Skip this version» — the version is saved to config.</summary>
    public event Action<string>? SkipRequested;

    public UpdateNotificationWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Shows the update notification positioned near the system tray area.</summary>
    public void ShowUpdate(UpdateInfo info)
    {
        _info = info;

        this.FindControl<TextBlock>("TitleText")!.Text = I18n.T("update.available_title");
        this.FindControl<TextBlock>("BodyText")!.Text = I18n.T("update.available_body", info.Version);

        this.FindControl<Button>("UpdateBtn")!.Content = I18n.T("update.btn_update");
        this.FindControl<Button>("LaterBtn")!.Content = I18n.T("update.btn_later");
        this.FindControl<Button>("SkipBtn")!.Content = I18n.T("update.btn_skip");

        // Reset to initial state
        this.FindControl<StackPanel>("ButtonsPanel")!.IsVisible = true;
        this.FindControl<StackPanel>("ProgressPanel")!.IsVisible = false;
        this.FindControl<ProgressBar>("DownloadProgress")!.Value = 0;

        PositionNearTray();
        base.Show();
    }

    private void PositionNearTray()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        const int margin = 12;
        Position = new PixelPoint(
            area.X + Math.Max(0, area.Width - (int)(Width * scale) - (int)(margin * scale)),
            area.Y + Math.Max(0, area.Height - (int)(350 * scale) - (int)(margin * scale)));
    }

    private async void OnUpdate(object? sender, RoutedEventArgs e)
    {
        if (_info is null) return;

        // Switch to progress mode
        this.FindControl<StackPanel>("ButtonsPanel")!.IsVisible = false;
        this.FindControl<StackPanel>("ProgressPanel")!.IsVisible = true;

        var progressBar = this.FindControl<ProgressBar>("DownloadProgress")!;
        var progressText = this.FindControl<TextBlock>("ProgressText")!;

        _downloadCts = new CancellationTokenSource();

        try
        {
            // Determine paths
            var currentExe = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "WorkFlowSync.exe");
            var updateExe = UpdateInstaller.UpdateExePath(currentExe);

            // Download with progress
            await UpdateDownloader.DownloadAsync(
                _info.AssetUrl,
                updateExe,
                percent => Dispatcher.UIThread.Post(() =>
                {
                    progressBar.Value = percent;
                    progressText.Text = I18n.T("update.downloading", percent);
                }),
                _downloadCts.Token);

            // Installing
            progressBar.Value = 100;
            progressText.Text = I18n.T("update.installing");

            // Brief pause so the user sees "Installing..."
            await Task.Delay(500);

            progressText.Text = I18n.T("update.restarting");

            UpdateRequested?.Invoke(_info);
        }
        catch (OperationCanceledException)
        {
            Hide();
        }
        catch (Exception ex)
        {
            progressText.Text = I18n.T("update.failed", ex.Message);
            // After a failure, show buttons again after a delay so the user can retry or dismiss
            await Task.Delay(3000);
            this.FindControl<StackPanel>("ButtonsPanel")!.IsVisible = true;
            this.FindControl<StackPanel>("ProgressPanel")!.IsVisible = false;
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private void OnLater(object? sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        LaterRequested?.Invoke();
        Hide();
    }

    private void OnSkip(object? sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        if (_info is not null)
            SkipRequested?.Invoke(_info.Version);
        Hide();
    }
}
