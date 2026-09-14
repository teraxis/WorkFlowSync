using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkFlowSync.Core.Config;

/// <summary>Root configuration document (config.json). See docs/product/features/configuration.md.</summary>
public sealed class SyncConfig
{
    /// <summary>Folder pairs to mirror. Each pair has its own state namespace.</summary>
    public List<FolderPair> Pairs { get; set; } = new();

    /// <summary>SQLite state database path. Relative paths resolve against the config file folder.</summary>
    public string StatePath { get; set; } = "state.db";

    /// <summary>Log folder. Relative paths resolve against the config file folder.</summary>
    public string LogPath { get; set; } = "logs";

    /// <summary>Interval between passes in resident (loop) mode.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Window appearance: follow Windows, or force light/dark.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;


    /// <summary>Directory listing buffer for the scanner; large values cut SMB round-trips.</summary>
    public int ScanBufferSize { get; set; } = 256 * 1024;

    /// <summary>Concurrent directory listings per pass.</summary>
    public int ScanParallelism { get; set; } = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static SyncConfig Parse(string json) =>
        JsonSerializer.Deserialize<SyncConfig>(json, JsonOptions)
        ?? throw new InvalidDataException("Config document is empty.");

    public static SyncConfig Load(string path) => Parse(File.ReadAllText(path));

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Returns human-readable problems; empty when the config is usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Pairs.Count == 0) problems.Add("No folder pairs configured.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Pairs)
        {
            if (string.IsNullOrWhiteSpace(pair.Name)) problems.Add("A pair has an empty name.");
            else if (!names.Add(pair.Name)) problems.Add($"Duplicate pair name '{pair.Name}'.");
            if (string.IsNullOrWhiteSpace(pair.Source)) problems.Add($"Pair '{pair.Name}': source is empty.");
            if (string.IsNullOrWhiteSpace(pair.Target)) problems.Add($"Pair '{pair.Name}': target is empty.");
            if (pair.Retention is { } r && r <= TimeSpan.Zero) problems.Add($"Pair '{pair.Name}': retention must be positive.");
        }
        if (Interval < TimeSpan.FromMinutes(1)) problems.Add("Interval must be at least 1 minute.");
        if (ScanParallelism is < 1 or > 64) problems.Add("ScanParallelism must be within 1..64.");
        if (ScanBufferSize < 4096) problems.Add("ScanBufferSize must be at least 4096.");
        return problems;
    }
}

/// <summary>One source → target mirror.</summary>
public sealed class FolderPair
{
    /// <summary>Stable identifier; renaming it orphans the pair's state.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The pair's own play/pause state: enabled pairs are checked automatically every <see cref="SyncConfig.Interval"/>,
    /// paused ones are skipped (running one by hand still works).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Network (or any) source root. Read-only for us.</summary>
    public string Source { get; set; } = "";

    /// <summary>Local mirror root (inside OneDrive).</summary>
    public string Target { get; set; } = "";

    public LinkMode Links { get; set; } = LinkMode.Follow;

    /// <summary>Keep only items first seen within this window; null = keep forever.</summary>
    public TimeSpan? Retention { get; set; }

    /// <summary>Path patterns (relative to source root) that are never mirrored.</summary>
    public List<string> Exclude { get; set; } = new();
}
