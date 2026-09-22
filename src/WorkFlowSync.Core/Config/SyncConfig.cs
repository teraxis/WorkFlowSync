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

    /// <summary>Log folder. Relative paths resolve against the config file folder (= the app folder).</summary>
    public string LogPath { get; set; } = "logs";

    /// <summary>Daily log files older than this many days are deleted (rotation).</summary>
    public int LogKeepDays { get; set; } = 30;

    /// <summary>
    /// Default interval between passes in resident (loop) mode. A pair may set its own
    /// (<see cref="FolderPair.Interval"/>); this is what the rest fall back to.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often this pair should be checked: its own setting, or the shared default.</summary>
    public TimeSpan IntervalFor(FolderPair pair) => pair.Interval ?? Interval;

    /// <summary>Window appearance: follow Windows, or force light/dark.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Interface language: "uk", "en", or "system". Default is "uk".</summary>
    public string Language { get; set; } = "uk";

    /// <summary>Whether a finished pass may show a message: everything, only problems, or nothing.</summary>
    public NotifyMode Notifications { get; set; } = NotifyMode.All;

    /// <summary>How many files the «Останні зміни» window lists. Capped so a million-row pair cannot reach it.</summary>
    public int RecentLimit { get; set; } = 40;

    /// <summary>Whether to check GitHub for newer versions on startup (and once a day while running).</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Release tag the user chose to skip (e.g. "0.2.0"). Null = remind about everything.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>
    /// Hour (0–23) from which routine messages are held back, and the hour they resume. Equal values mean
    /// no quiet period. The range may cross midnight (22 → 8 is the obvious one). Problems are always shown:
    /// a quiet evening is a reason not to be told about twelve new files, not a reason to hide a failure.
    /// </summary>
    public int QuietHoursFrom { get; set; }
    public int QuietHoursTo { get; set; }


    /// <summary>Directory listing buffer for the scanner; large values cut SMB round-trips.</summary>
    public int ScanBufferSize { get; set; } = 256 * 1024;

    /// <summary>Concurrent directory listings per pass (upper bound; <see cref="CpuLoad"/> may lower it).</summary>
    public int ScanParallelism { get; set; } = 8;

    /// <summary>How much of the machine a pass may take: balanced (default), full speed, or low.</summary>
    public CpuLoad CpuLoad { get; set; } = CpuLoad.Balanced;

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
            if (pair.MaxAge is { } ma && ma <= TimeSpan.Zero) problems.Add($"Pair '{pair.Name}': maxAge must be positive.");
            if (pair.AutoClean is { } ac && ac <= TimeSpan.Zero) problems.Add($"Pair '{pair.Name}': autoClean must be positive.");
            if (pair.AutoClean is not null && pair.Mode == SyncMode.TwoWay)
                problems.Add($"Pair '{pair.Name}': autoClean is a mirror-only setting; a two-way pair propagates real deletions instead.");
            if (pair.RequireApproval && pair.Mode == SyncMode.Mirror)
                problems.Add($"Pair '{pair.Name}': requireApproval is a twoWay-only setting; mirror source is read-only.");
            if (pair.VersionsKeepDays is < 1 or > 3650)
                problems.Add($"Pair '{pair.Name}': versionsKeepDays must be between 1 and 3650.");
            if (pair.VersionsMaxGb is < 1 or > 1000)
                problems.Add($"Pair '{pair.Name}': versionsMaxGb must be between 1 and 1000.");
            if (pair.Interval is { } pi && pi < TimeSpan.FromMinutes(1)) problems.Add($"Pair '{pair.Name}': interval must be at least 1 minute.");
            if (pair.MinFreeSpaceGb is < 1 or > 1000)
                problems.Add($"Pair '{pair.Name}': minFreeSpaceGb must be between 1 and 1000.");
            if (pair.RotateOnLowSpace && !pair.DiskQuotaEnabled)
                problems.Add($"Pair '{pair.Name}': rotateOnLowSpace requires diskQuotaEnabled.");
            if (Nesting(pair.Source, pair.Target) is { } nest) problems.Add($"Pair '{pair.Name}': {nest}");
        }
        if (Interval < TimeSpan.FromMinutes(1)) problems.Add("Interval must be at least 1 minute.");
        if (ScanParallelism is < 1 or > 64) problems.Add("ScanParallelism must be within 1..64.");
        if (ScanBufferSize < 4096) problems.Add("ScanBufferSize must be at least 4096.");
        if (LogKeepDays is < 1 or > 3650) problems.Add("LogKeepDays must be within 1..3650.");
        if (RecentLimit is < 5 or > 500) problems.Add("RecentLimit must be within 5..500.");
        if (!string.IsNullOrWhiteSpace(Language) &&
            !Language.Equals("uk", StringComparison.OrdinalIgnoreCase) &&
            !Language.Equals("en", StringComparison.OrdinalIgnoreCase) &&
            !Language.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"Language '{Language}' is unsupported; choose 'uk', 'en', or 'system'.");
        }
        return problems;
    }

    /// <summary>
    /// A pair inside itself: the two roots are the same folder, or one contains the other. Every pass would then
    /// copy the copy, so such a pair is rejected. Returns the problem, or null when the two roots are unrelated.
    /// </summary>
    public static string? Nesting(string? source, string? target)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return null;
        string Norm(string p)
        {
            try { return Path.GetFullPath(p.Trim()).TrimEnd('\\', '/'); }
            catch { return p.Trim().TrimEnd('\\', '/'); }
        }
        var a = Norm(source);
        var b = Norm(target);
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return "source and target are the same folder.";
        if (b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase)) return "target is inside the source folder.";
        if (a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase)) return "source is inside the target folder.";
        return null;
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

    /// <summary>Destination root: a local, removable, network, or Windows Files On-Demand folder.</summary>
    public string Target { get; set; } = "";

    /// <summary>One-way mirror with memory (default) or full two-way synchronisation.</summary>
    public SyncMode Mode { get; set; } = SyncMode.Mirror;

    public LinkMode Links { get; set; } = LinkMode.Follow;

    /// <summary>
    /// How often this pair is checked. Null = use <see cref="SyncConfig.Interval"/>, which is what every
    /// pair did before 2026-09-16 and what a config written then still means. Pairs differ in how fast
    /// they change and how expensive they are to scan, so a single number for all of them was wrong.
    /// </summary>
    public TimeSpan? Interval { get; set; }

    /// <summary>
    /// React to changes as they happen, or only on the interval. Absent in older configs, which read
    /// as <see cref="WatchMode.Auto"/>. The periodic pass runs either way — watching only shortens the wait.
    /// </summary>
    public WatchMode Watch { get; set; } = WatchMode.Auto;

    /// <summary>
    /// Cloud-only files (OneDrive Files On-Demand) when this pair has to copy one out: leave it alone
    /// (default) or download it. Only relevant in two-way mode — a mirror never reads its target.
    /// </summary>
    public CloudFileMode CloudFiles { get; set; } = CloudFileMode.Skip;

    /// <summary>
    /// After writing a file to the target, request the Windows Files On-Demand «online-only» state.
    /// The target must be inside a registered cloud sync root (normally OneDrive). False preserves the
    /// historical behaviour and keeps copied bytes locally available.
    /// </summary>
    public bool FreeUpSpaceAfterCopy { get; set; }

    /// <summary>
    /// Protect the configured free-space reserve on the target volume independently of cloud storage.
    /// </summary>
    public bool DiskQuotaEnabled { get; set; }

    /// <summary>Free disk space kept ahead of the next target write while <see cref="DiskQuotaEnabled"/> is on.</summary>
    public int MinFreeSpaceGb { get; set; } = 5;

    /// <summary>
    /// When the reserve is exhausted on any target, permanently delete the oldest intact copies owned by
    /// this pair. Never applies to the source side. A cloud provider may propagate the deletion.
    /// </summary>
    public bool RotateOnLowSpace { get; set; }

    /// <summary>
    /// Intake filter: copy only items whose appearance date (<c>first_seen</c>) falls within this window;
    /// null = copy whatever the source has. Older files stay in the source untouched — they are simply
    /// never copied and never updated here. Nothing is ever removed from the target because of this.
    /// Mirror mode only.
    /// </summary>
    public TimeSpan? MaxAge { get; set; }

    /// <summary>
    /// Auto-clean: our own copies whose appearance date is older than this window go to the Recycle Bin;
    /// null = keep them forever. Age is measured by the date WE recorded, not by ctime/mtime. The source
    /// is never touched. Mirror mode only — a two-way pair propagates real deletions instead.
    /// </summary>
    public TimeSpan? AutoClean { get; set; }

    /// <summary>
    /// Configs written before 2026-09-17 carried one <c>retention</c> window whose label promised an intake
    /// filter. It is read as <see cref="MaxAge"/> and never written back, so an old config keeps the meaning
    /// its checkbox showed. Auto-clean is opt-in and starts off. Write-only on purpose: a property with no
    /// getter is read by the serializer and never written back, so the legacy name dies on the first save.
    /// </summary>
    public TimeSpan? Retention { set => MaxAge ??= value; }

    /// <summary>Path patterns (relative to source root) that are never mirrored.</summary>
    public List<string> Exclude { get; set; } = new();

    /// <summary>Approval mode: changes in target folder B require explicit human confirmation (docs/plan-etap6.md §7).</summary>
    public bool RequireApproval { get; set; }

    /// <summary>Which side is moderated. Defaults to "target" (the local folder).</summary>
    public string ApprovalSide { get; set; } = "target";

    /// <summary>Whether to preserve version snapshots in .wfsversions before revert/deletion.</summary>
    public bool Versioning { get; set; }

    /// <summary>Subfolder for versions inside target root. Defaults to ".wfsversions".</summary>
    public string VersionsPath { get; set; } = ".wfsversions";

    /// <summary>How many days to keep version snapshots (1..3650, default 30).</summary>
    public int VersionsKeepDays { get; set; } = 30;

    /// <summary>Maximum size in GB for .wfsversions folder before oldest snapshots are pruned (1..1000, default 5).</summary>
    public int VersionsMaxGb { get; set; } = 5;
}
