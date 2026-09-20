using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using WorkFlowSync.App.ViewModels;

namespace WorkFlowSync.App.Views;

/// <summary>
/// The window the tray icon opens: what has appeared in the watched folders lately
/// (docs/plan-etap5.md §5.6). Borderless, in the corner of the work area, and gone as soon as it loses
/// focus — the same manners as the notification-area windows people already know.
/// </summary>
public partial class RecentChangesWindow : Window
{
    /// <summary>Raised when the user asks for the ordinary application window instead.</summary>
    public event Action? MainWindowRequested;

    /// <summary>Raised when the user clicks the approval banner to open the approval window.</summary>
    public event Action? ApprovalWindowRequested;

    public RecentChangesWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => Hide();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Reloads the list and places the window in the corner of the screen the tray lives on.</summary>
    public void ShowNearTray(RecentChangesViewModel model)
    {
        DataContext = model;
        model.Load();

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null)
        {
            // The work area already excludes the taskbar, wherever the user keeps it.
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            var w = (int)(Width * scale);
            var h = (int)(Height * scale);
            const int margin = 12;
            Position = new Avalonia.PixelPoint(
                area.X + Math.Max(0, area.Width - w - (int)(margin * scale)),
                area.Y + Math.Max(0, area.Height - h - (int)(margin * scale)));
        }

        Show();
        Activate();
    }

    private void OnItemActivated(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source && (source is Button || source.FindAncestorOfType<Button>() is not null))
        {
            // Click was on the folder button or inside it, don't trigger item opening
            return;
        }

        if ((sender as ListBox)?.SelectedItem is RecentItemViewModel item && item.CanOpen && !string.IsNullOrEmpty(item.FullPath))
        {
            OpenFile(item.FullPath);
        }
    }

    /// <summary>The folder button on a row: opens Explorer with the file selected.</summary>
    private void OnRevealClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Button)?.Tag is string path && !string.IsNullOrEmpty(path))
        {
            RevealFolder(path);
        }
    }

    private void OnOpenMainWindow(object? sender, RoutedEventArgs e)
    {
        Hide();
        MainWindowRequested?.Invoke();
    }

    private void OnOpenApprovalClicked(object? sender, RoutedEventArgs e)
    {
        Hide();
        ApprovalWindowRequested?.Invoke();
    }

    /// <summary>Opens the file directly with its default associated application.</summary>
    private static void OpenFile(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            }
            else
            {
                RevealFolder(fullPath);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cannot open file {fullPath}: {ex.Message}");
        }
    }

    /// <summary>Opens Explorer with the file selected; falls back to its folder when the file is gone.</summary>
    private static void RevealFolder(string fullPath)
    {
        try
        {
            var target = File.Exists(fullPath) ? $"/select,\"{fullPath}\"" : $"\"{Path.GetDirectoryName(fullPath)}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cannot reveal {fullPath}: {ex.Message}");
        }
    }
}
