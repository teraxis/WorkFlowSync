namespace WorkFlowSync.Core;

/// <summary>Static product metadata shared by CLI and diagnostics.</summary>
public static class WorkFlowSyncInfo
{
    public const string Name = "WorkFlowSync";

    public static string Version =>
        typeof(WorkFlowSyncInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public const string Author = "Білик Ігор";
    public const string AuthorEnglish = "Bilyk Ihor";
    public const string AuthorEmail = "teraxis@gmail.com";
    public const string GitHubRepo = "https://github.com/teraxis/WorkFlowSync";
    public const string GitHubAuthor = "https://github.com/teraxis";
    public const string License = "MIT License";
    public const string Copyright = "© 2026 Білик Ігор. MIT License.";
}
