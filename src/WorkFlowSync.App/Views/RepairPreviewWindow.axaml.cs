using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.I18n;

namespace WorkFlowSync.App.Views;

public partial class RepairPreviewWindow : Window
{
    private TargetRepair.Plan? _plan;

    public RepairPreviewWindow() => InitializeComponent();

    public static Task<bool> ShowAsync(Window owner, TargetRepair.Plan plan)
    {
        var window = new RepairPreviewWindow { _plan = plan };
        window.Title = I18n.T("repair.preview_title");
        window.HintText.Text = I18n.T("repair.preview_hint");
        window.ExportButton.Content = I18n.T("troubleshoot.export");
        window.CancelButton.Content = I18n.T("common.cancel");
        window.ApplyButton.Content = I18n.T("troubleshoot.apply_paths");
        window.ApplyButton.IsEnabled = plan.ActionCount > 0;
        static string Gb(long bytes) => (bytes / 1_000_000_000d).ToString("N2");
        var full = plan.Rows.Count(r => r.OldPath.Length > 260 || r.NewPath.Length > 260);
        var segments240 = plan.Rows.Count(r => r.OldPath.Split('\\').Any(s => System.Text.Encoding.UTF8.GetByteCount(s) > 240));
        var segments200 = plan.Rows.Count(r => r.OldPath.Split('\\').Any(s => System.Text.Encoding.UTF8.GetByteCount(s) > 200));
        window.SummaryText.Text = I18n.T("repair.preview_summary", plan.KeepFiles, Gb(plan.KeepBytes),
            plan.RenameFiles, Gb(plan.RenameBytes), plan.RecycleFiles, Gb(plan.RecycleBytes),
            plan.Conflicts, full, segments240, segments200);
        window.RowsText.Text = string.Join(Environment.NewLine, plan.Rows.Take(100)
            .Select(r => $"{r.Action}: {r.OldPath} → {r.NewPath} {r.Detail}"));
        return window.ShowDialog<bool>(owner);
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new()
        {
            SuggestedFileName = $"repair-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
        });
        if (file?.TryGetLocalPath() is { } path)
            TargetRepair.WriteCsv(_plan, path);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    private void OnApply(object? sender, RoutedEventArgs e) => Close(true);
}
