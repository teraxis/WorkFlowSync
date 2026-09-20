using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Core.Watching;

/// <summary>What the watcher decided should happen once the dust settled.</summary>
public sealed record ChangeBatch
{
    /// <summary>Directories to re-scan, relative to the pair root. Empty when a full pass is wanted instead.</summary>
    public required IReadOnlyList<string> Directories { get; init; }

    /// <summary>True when a partial pass is not good enough and the whole pair has to be looked at.</summary>
    public bool NeedsFullPass { get; init; }

    /// <summary>Why a full pass was asked for; null otherwise. Goes straight into the log.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Turns a storm of file-system events into a short list of folders worth re-scanning
/// (docs/plan-etap5.md §4.3 and §4.8). Deliberately free of any file-system access and of any timer:
/// the caller supplies the clock, so every rule here is testable to the millisecond.
///
/// The rules it implements:
/// <list type="number">
/// <item>events are coalesced to the DIRECTORY they happened in, never kept per file;</item>
/// <item>a directory is only handed out once it has been quiet for <see cref="Options.Quiet"/> — a long
/// save must not be scanned halfway through;</item>
/// <item>…but never later than <see cref="Options.MaxDelay"/> after the first event, so a folder someone
/// keeps touching is still synchronised;</item>
/// <item>excluded and temporary files never enter the queue at all;</item>
/// <item>too many folders at once stops being a hint and becomes a full pass.</item>
/// </list>
/// </summary>
public sealed class ChangeQueue
{
    public sealed record Options
    {
        /// <summary>A path must be untouched this long before it is worth scanning.</summary>
        public TimeSpan Quiet { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>Upper bound on holding anything back, however busy the folder is.</summary>
        public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Above this many folders a partial pass is no longer the cheaper option.</summary>
        public int MaxDirectories { get; init; } = 200;
    }

    /// <summary>
    /// Editor and download droppings. Kept apart from the user's own excludes (F6) because this is not a
    /// matter of taste: acting on these produces churn and half-written files, never useful work.
    /// </summary>
    public static readonly IReadOnlyList<string> TemporaryPatterns = new[]
    {
        "~$*",          // Word, Excel, PowerPoint owner files
        ".~lock.*",     // LibreOffice
        "*.tmp", "*.temp",
        "*.partial", "*.part", "*.crdownload",   // browsers and downloaders
        "*.swp", "*~",  // editors
        "*.wfs-tmp",    // our own copy-then-rename temp name
    };

    private static readonly ExcludeMatcher Temporary = new(TemporaryPatterns);

    private readonly Options _options;
    private readonly ExcludeMatcher _excludes;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    /// <summary>Directory (relative) → when it was last touched.</summary>
    private readonly Dictionary<string, DateTimeOffset> _pending = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset? _firstEvent;
    private string? _fullPassReason;

    public ChangeQueue(Options? options = null, ExcludeMatcher? excludes = null, Func<DateTimeOffset>? clock = null)
    {
        _options = options ?? new Options();
        _excludes = excludes ?? ExcludeMatcher.Empty;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Folders waiting to be scanned right now (for diagnostics and tests).</summary>
    public int PendingDirectories { get { lock (_gate) return _pending.Count; } }

    public bool FullPassWanted { get { lock (_gate) return _fullPassReason is not null; } }

    /// <summary>True when the path is one this program should never react to.</summary>
    public bool IsNoise(string relativePath) =>
        string.IsNullOrWhiteSpace(relativePath) || Temporary.IsExcluded(relativePath) || _excludes.IsExcluded(relativePath);

    /// <summary>
    /// Records one event. Must stay this cheap: the caller is a file-system notification handler, and while
    /// it runs the kernel buffer keeps filling (docs §4.3, rule 1).
    /// </summary>
    public void Add(string relativePath)
    {
        if (IsNoise(relativePath)) return;

        var dir = DirectoryOf(relativePath);
        var now = _clock();
        lock (_gate)
        {
            _firstEvent ??= now;
            _pending[dir] = now;
            if (_pending.Count > _options.MaxDirectories)
                _fullPassReason ??= $"more than {_options.MaxDirectories} folders changed at once";
        }
    }

    /// <summary>
    /// Whatever the reason — a lost notification, an overflowed buffer, a re-subscription — the only safe
    /// answer is to look at everything (docs §4.3, rules 3 and 4).
    /// </summary>
    public void RequestFullPass(string reason)
    {
        lock (_gate)
        {
            _fullPassReason ??= reason;
        }
    }

    /// <summary>
    /// Returns work that is ready, or null while everything is still settling. Taking a batch clears it:
    /// the caller owns it from then on.
    /// </summary>
    public ChangeBatch? TakeReady()
    {
        var now = _clock();
        lock (_gate)
        {
            if (_fullPassReason is { } reason)
            {
                Reset();
                return new ChangeBatch { Directories = Array.Empty<string>(), NeedsFullPass = true, Reason = reason };
            }

            if (_pending.Count == 0) return null;

            // Past the ceiling on patience, everything goes at once; otherwise only what has gone quiet.
            var overdue = _firstEvent is { } first && now - first >= _options.MaxDelay;
            var ready = overdue
                ? _pending.Keys.ToList()
                : _pending.Where(p => now - p.Value >= _options.Quiet).Select(p => p.Key).ToList();
            if (ready.Count == 0) return null;

            foreach (var dir in ready) _pending.Remove(dir);
            if (_pending.Count == 0) _firstEvent = null;

            // Parents first: scanning a parent already covers its children, and the planner wants that order.
            ready.Sort((a, b) =>
            {
                var depth = Depth(a).CompareTo(Depth(b));
                return depth != 0 ? depth : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
            return new ChangeBatch { Directories = ready, NeedsFullPass = false };
        }
    }

    public void Clear()
    {
        lock (_gate) Reset();
    }

    private void Reset()
    {
        _pending.Clear();
        _firstEvent = null;
        _fullPassReason = null;
    }

    /// <summary>
    /// The folder to look at because of this event. «Тека\файл.docx» means «look at Тека».
    ///
    /// A top-level name is ambiguous — it is either a file sitting in the pair root, or a folder. The queue
    /// does no I/O, so it hands back the name itself as a CANDIDATE and lets the caller check: a real folder
    /// becomes the scope, anything else means the whole pair. Returning "the root" here instead looked
    /// tidier and was wrong: writing a file into «Тека» also raises an event for «Тека», so every single
    /// save turned into a full pass and partial passes never ran at all (caught by the integration tests).
    /// </summary>
    public static string DirectoryOf(string relativePath)
    {
        var trimmed = relativePath.Replace('/', '\\').Trim('\\');
        var slash = trimmed.LastIndexOf('\\');
        return slash <= 0 ? trimmed : trimmed[..slash];
    }

    private static int Depth(string path) => path.Length == 0 ? 0 : path.Count(c => c == '\\') + 1;
}
