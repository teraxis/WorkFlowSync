using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Tests;

/// <summary>
/// A scoped pass over real folders: scanner + state + planner + executor together (docs/plan-etap5.md §5.3).
/// The planner tests prove the rules; these prove the wiring — that the runner really hands the planner only
/// the subtree, and that everything outside it comes back untouched.
/// </summary>
[Trait("Category", "E2E")]
public sealed class ScopedPassEndToEndTests : IDisposable
{
    private const string Watched = "Тека А";
    private const string Other = "Тека Б";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-scope-" + Guid.NewGuid().ToString("N"));
    private readonly string _src;
    private readonly string _dst;
    private readonly string _configPath;
    private readonly SyncConfig _config;

    public ScopedPassEndToEndTests()
    {
        _src = Path.Combine(_root, "src");
        _dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(_src);
        _configPath = Path.Combine(_root, "config.json");
        _config = new SyncConfig
        {
            Pairs = { new FolderPair { Name = "t", Source = _src, Target = _dst } },
            StatePath = "state.db",
            LogPath = "logs",
            ScanParallelism = 2,
        };
        ConfigFile.Save(_config, _configPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private PassResult Pass(string? scope = null, string? pair = null) =>
        new SyncRunner(_config, _configPath, new MemorySyncLog()).Run(dryRun: false, forceBackfill: false,
            CancellationToken.None, pair ?? (scope is null ? null : "t"), scope);

    private void WriteSrc(string rel, string content)
    {
        var p = Path.Combine(_src, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private Dictionary<string, StateEntry> State()
    {
        using var store = new StateStore(Path.Combine(_root, "state.db"));
        return store.Load("t");
    }

    private bool InMirror(string rel) => File.Exists(Path.Combine(_dst, rel)) || Directory.Exists(Path.Combine(_dst, rel));

    /// <summary>Two folders mirrored, then the user deletes a file from one of them locally.</summary>
    private void GivenAMirrorWithALocalDeletionOutsideTheScope()
    {
        WriteSrc($@"{Watched}\перший.txt", "a");
        WriteSrc($@"{Other}\другий.txt", "b");
        Assert.Equal(0, Pass().Errors);
        Assert.True(InMirror($@"{Other}\другий.txt"));

        File.Delete(Path.Combine(_dst, Other, "другий.txt"));
    }

    [Fact]
    public void A_new_file_in_the_watched_folder_arrives_without_a_full_pass()
    {
        GivenAMirrorWithALocalDeletionOutsideTheScope();
        WriteSrc($@"{Watched}\новий.txt", "new");

        var pass = Pass(scope: Watched);

        Assert.Equal(0, pass.Errors);
        Assert.Equal(Watched, Assert.Single(pass.Pairs).Scope);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_dst, Watched, "новий.txt")));
    }

    [Fact]
    public void The_file_deleted_outside_the_scope_is_left_alone_and_then_tombstoned_by_the_full_pass()
    {
        GivenAMirrorWithALocalDeletionOutsideTheScope();
        WriteSrc($@"{Watched}\новий.txt", "new");

        Pass(scope: Watched);

        // The scoped pass saw nothing of «Тека Б», so it must not have concluded anything about it.
        Assert.Equal(EntryStatus.Active, State()[$@"{Other}\другий.txt"].Status);

        // Deferred, not lost: the next full pass reaches the conclusion the scoped one refused to reach.
        Assert.Equal(0, Pass().Errors);
        Assert.Equal(EntryStatus.Tombstone, State()[$@"{Other}\другий.txt"].Status);
    }

    [Fact]
    public void A_file_deleted_inside_the_scope_is_not_tombstoned_either()
    {
        // Even where it CAN see the whole subtree, a scoped pass does not decide that something is gone:
        // the scan may have silently skipped an unreadable folder, and a tombstone can never be undone.
        WriteSrc($@"{Watched}\перший.txt", "a");
        Pass();
        File.Delete(Path.Combine(_dst, Watched, "перший.txt"));

        Pass(scope: Watched);

        Assert.Equal(EntryStatus.Active, State()[$@"{Watched}\перший.txt"].Status);
    }

    [Fact]
    public void Retention_does_not_fire_in_a_scoped_pass()
    {
        _config.Pairs[0].AutoClean = TimeSpan.FromDays(1);
        WriteSrc($@"{Watched}\старий.txt", "old");
        Pass();

        // Backdate first_seen so the file is well past the retention window.
        using (var store = new StateStore(Path.Combine(_root, "state.db")))
        {
            var row = store.Load("t")[$@"{Watched}\старий.txt"];
            store.Upsert(new[] { row with { FirstSeenUtc = DateTimeOffset.UtcNow.AddYears(-5) } });
        }

        Pass(scope: Watched);
        Assert.True(InMirror($@"{Watched}\старий.txt"), "a scoped pass must not expire anything");

        Pass();
        Assert.False(InMirror($@"{Watched}\старий.txt"), "the full pass still applies retention");
    }

    [Fact]
    public void First_seen_is_not_backdated_just_because_the_scope_is_empty()
    {
        // An empty scope used to look exactly like an empty pair, which would have triggered the first-pass
        // backfill and rewritten first_seen for everything it touched — taking retention with it.
        WriteSrc($@"{Watched}\перший.txt", "a");
        Pass();
        var original = State()[$@"{Watched}\перший.txt"].FirstSeenUtc;

        WriteSrc($@"{Other}\новий.txt", "b");
        Pass(scope: Other);

        Assert.Equal(original, State()[$@"{Watched}\перший.txt"].FirstSeenUtc);
        var added = State()[$@"{Other}\новий.txt"];
        Assert.True(added.FirstSeenUtc > DateTimeOffset.UtcNow.AddMinutes(-5), "a file seen now is first seen now");
    }

    [Fact]
    public void A_scope_that_vanished_from_the_source_is_reported_and_changes_nothing()
    {
        WriteSrc($@"{Watched}\перший.txt", "a");
        Pass();
        Directory.Delete(Path.Combine(_src, Watched), recursive: true);

        var pass = Pass(scope: Watched);

        Assert.Equal(0, pass.Errors);
        Assert.False(Assert.Single(pass.Pairs).Skipped, "the pair root is still there; only the folder is gone");
        Assert.Equal(EntryStatus.Active, State()[$@"{Watched}\перший.txt"].Status);
        Assert.True(InMirror($@"{Watched}\перший.txt"), "the local copy must survive: source deletions never propagate");
    }

    [Fact]
    public void The_watched_folder_itself_is_not_mistaken_for_a_deletion()
    {
        // A scan only reports children, so a scoped scan that started inside «Тека А» never mentions «Тека А».
        // The state does have a row for it — without the scanner adding it back, every scoped pass would look
        // like "the watched folder is gone" and rack up deferred decisions (caught by running it for real).
        WriteSrc($@"{Watched}\перший.txt", "a");
        Pass();
        WriteSrc($@"{Watched}\новий.txt", "new");

        var pass = Pass(scope: Watched);

        Assert.Equal(0, Assert.Single(pass.Pairs).Plan!.DeferredToFullPass);
        Assert.Equal(1, Assert.Single(pass.Pairs).Plan!.New);
    }

    [Fact]
    public void A_scope_that_does_not_exist_yet_in_the_mirror_is_created()
    {
        WriteSrc($@"{Watched}\перший.txt", "a");
        Pass();
        WriteSrc($@"{Other}\глибоко\новий.txt", "deep");

        Assert.Equal(0, Pass(scope: Other).Errors);

        Assert.Equal("deep", File.ReadAllText(Path.Combine(_dst, Other, "глибоко", "новий.txt")));
    }

    [Fact]
    public void A_scope_needs_exactly_one_pair()
    {
        _config.Pairs.Add(new FolderPair { Name = "t2", Source = _src, Target = Path.Combine(_root, "dst2") });
        ConfigFile.Save(_config, _configPath);
        var log = new MemorySyncLog();

        var pass = new SyncRunner(_config, _configPath, log).Run(dryRun: false, forceBackfill: false,
            CancellationToken.None, onlyPair: null, scope: Watched);

        Assert.Empty(pass.Pairs);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Error && l.Message.Contains("--scope", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dry_run_scoped_pass_changes_nothing_on_disk()
    {
        WriteSrc($@"{Watched}\перший.txt", "a");
        Pass();
        WriteSrc($@"{Watched}\новий.txt", "new");

        var pass = new SyncRunner(_config, _configPath, new MemorySyncLog())
            .Run(dryRun: true, forceBackfill: false, CancellationToken.None, "t", Watched);

        Assert.Equal(0, pass.Errors);
        Assert.False(InMirror($@"{Watched}\новий.txt"));
        Assert.DoesNotContain($@"{Watched}\новий.txt", State().Keys);
    }
}
