using Avalonia.Controls;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.I18n;

namespace WorkFlowSync.App.Views;

public partial class RepairProgressWindow : Window
{
    public RepairProgressWindow()
    {
        InitializeComponent();
        Title = I18n.T("troubleshoot.title");
        StageText.Text = I18n.T("repair.progress_start");
    }

    public void Update(RepairProgress progress)
    {
        StageText.Text = progress.Stage switch
        {
            "scan_source" => I18n.T("repair.progress_source"),
            "scan_target" => I18n.T("repair.progress_target"),
            "build" => I18n.T("repair.progress_plan", progress.Completed, progress.Total),
            "apply" => I18n.T("repair.progress_apply", progress.Completed, progress.Total),
            _ => I18n.T("repair.progress_start")
        };
        StageProgress.IsIndeterminate = progress.Total <= 0;
        if (progress.Total > 0)
        {
            StageProgress.Maximum = progress.Total;
            StageProgress.Value = progress.Completed;
        }
    }
}
