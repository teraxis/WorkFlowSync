using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.I18n;

namespace WorkFlowSync.App.Views;

public sealed record RecoveryPreviewResult(bool Apply, bool RestoreDeleted);

public partial class RecoveryPreviewWindow : Window
{
    private MissingFilePlan? _plan;
    private bool _restoreMode;

    public RecoveryPreviewWindow() => InitializeComponent();

    public static Task<RecoveryPreviewResult?> ShowAsync(Window owner, MissingFilePlan plan, bool restoreMode)
    {
        var window = new RecoveryPreviewWindow { _plan = plan, _restoreMode = restoreMode };
        window.Title = I18n.T("troubleshoot.audit_title");
        window.SummaryText.Text = I18n.T("troubleshoot.audit_summary", plan.Restorable, plan.Deleted,
            plan.Excluded, plan.Conflicts, plan.TargetOnlyFiles);
        window.ListHintText.Text = I18n.T("troubleshoot.list_hint");
        window.RowsText.Text = string.Join(Environment.NewLine, plan.Items.Take(100)
            .Select(i => $"{I18n.T($"troubleshoot.kind_{i.Kind}")}: {i.SourcePath} → {i.TargetPath}"));
        window.RestoreDeletedCheck.Content = I18n.T("troubleshoot.confirm_deleted", plan.Deleted);
        window.RestoreDeletedCheck.IsVisible = restoreMode && plan.Deleted > 0;
        window.ExportButton.Content = I18n.T("troubleshoot.export");
        window.CancelButton.Content = I18n.T("common.cancel");
        window.ApplyButton.Content = I18n.T(restoreMode ? "troubleshoot.apply" : "troubleshoot.close");
        return window.ShowDialog<RecoveryPreviewResult?>(owner);
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new()
        {
            SuggestedFileName = $"recovery-audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("kind,source_path,target_path");
        foreach (var item in _plan.Items)
            writer.WriteLine($"{item.Kind},{Csv(item.SourcePath)},{Csv(item.TargetPath)}");
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    private void OnApply(object? sender, RoutedEventArgs e) =>
        Close(new RecoveryPreviewResult(_restoreMode, RestoreDeletedCheck.IsChecked == true));
}
