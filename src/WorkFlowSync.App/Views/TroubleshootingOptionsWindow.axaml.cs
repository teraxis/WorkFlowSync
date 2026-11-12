using Avalonia.Controls;
using Avalonia.Interactivity;
using WorkFlowSync.Core.I18n;

namespace WorkFlowSync.App.Views;

public sealed record TroubleshootingOptions(bool RepairPaths, bool AuditMissing, bool RestoreMissing);

public partial class TroubleshootingOptionsWindow : Window
{
    public TroubleshootingOptionsWindow() => InitializeComponent();

    public static Task<TroubleshootingOptions?> ShowAsync(Window owner, bool mirror, bool shortenEnabled,
        bool repairRequired, bool wasEnabled)
    {
        var window = new TroubleshootingOptionsWindow();
        window.Title = I18n.T("troubleshoot.title");
        window.HeadingText.Text = I18n.T("troubleshoot.title");
        window.ExplanationText.Text = I18n.T("troubleshoot.explanation");
        window.PathsCheck.Content = I18n.T("troubleshoot.paths");
        window.PathsCheck.IsEnabled = mirror && shortenEnabled;
        window.PathsCheck.IsChecked = mirror && shortenEnabled && repairRequired;
        window.PathsUnavailableText.Text = I18n.T(mirror ? "troubleshoot.paths_enable_shortening" : "troubleshoot.mirror_only");
        window.PathsUnavailableText.IsVisible = !window.PathsCheck.IsEnabled;
        window.AuditCheck.Content = I18n.T("troubleshoot.audit");
        window.AuditCheck.IsEnabled = mirror;
        window.AuditCheck.IsChecked = false;
        window.RestoreCheck.Content = I18n.T("troubleshoot.restore");
        window.RestoreCheck.IsEnabled = mirror;
        window.RestoreCheck.IsChecked = false;
        window.AuditUnavailableText.Text = I18n.T("troubleshoot.mirror_only");
        window.AuditUnavailableText.IsVisible = !mirror;
        window.PauseHintText.Text = I18n.T(wasEnabled ? "troubleshoot.pause_resume" : "troubleshoot.stay_paused");
        window.CancelButton.Content = I18n.T("common.cancel");
        window.ContinueButton.Content = I18n.T("troubleshoot.continue");
        window.UpdateContinue();
        return window.ShowDialog<TroubleshootingOptions?>(owner);
    }

    private void OnSelectionChanged(object? sender, RoutedEventArgs e) => UpdateContinue();

    private void UpdateContinue()
    {
        if (ContinueButton is null) return;
        ContinueButton.IsEnabled = PathsCheck?.IsChecked == true || AuditCheck?.IsChecked == true ||
            RestoreCheck?.IsChecked == true;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnContinue(object? sender, RoutedEventArgs e)
    {
        var paths = PathsCheck.IsEnabled && PathsCheck.IsChecked == true;
        var restore = RestoreCheck.IsChecked == true;
        var audit = AuditCheck.IsChecked == true || restore;
        Close(paths || audit ? new TroubleshootingOptions(paths, audit, restore) : null);
    }
}
