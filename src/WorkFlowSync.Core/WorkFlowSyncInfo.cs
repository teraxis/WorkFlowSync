namespace WorkFlowSync.Core;

/// <summary>Static product metadata shared by CLI and diagnostics.</summary>
public static class WorkFlowSyncInfo
{
    public const string Name = "WorkFlowSync";

    public static string Version =>
        typeof(WorkFlowSyncInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
