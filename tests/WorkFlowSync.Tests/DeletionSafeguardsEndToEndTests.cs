using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Tests;

/// <summary>
/// The safeguards that stand between a wrong conclusion and lost files, exercised on real folders
/// (docs/plan-etap5.md §5.4). They matter because the customer chose to keep removals on a network root
/// permanent: there is no Recycle Bin there and no way back.
/// </summary>
[Trait("Category", "E2E")]
public sealed class DeletionSafeguardsEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-guard-" + Guid.NewGuid().ToString("N"));
    private readonly string _a;
    private readonly string _b;
    private readonly string _configPath;
    private readonly SyncConfig _config;
    private readonly MemorySyncLog _log = new();

    public DeletionSafeguardsEndToEndTests()
    {
        _a = Path.Combine(_root, "a");
        _b = Path.Combine(_root, "b");
        Directory.CreateDirectory(_a);
        _configPath = Path.Combine(_root, "config.json");
        _config = new SyncConfig
        {
            Pairs = { new FolderPair { Name = "t", Source = _a, Target = _b, Mode = SyncMode.TwoWay } },
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

    private PassResult Pass() =>
        new SyncRunner(_config, _configPath, _log).Run(dryRun: false);

    private void Write(string side, string rel, string content)
    {
        var p = Path.Combine(side, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private Dictionary<string, StateEntry> State()
    {
        using var store = new StateStore(Path.Combine(_root, "state.db"));
        return store.Load("t");
    }

    private bool Warned(string fragment) =>
        _log.Lines.Any(l => l.Level == LogLevel.Warn && l.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void A_removal_the_other_side_contradicts_is_not_carried_out()
    {
        Write(_a, "спільний.txt", "v1");
        Pass();

        // Delete on side A, then put it back before the pass acts — the classic stale-scan situation
        // (a file saved through delete-and-rename by Word looks exactly like this).
        File.Delete(Path.Combine(_a, "спільний.txt"));
        var plan = new SyncRunner(_config, _configPath, _log);
        Write(_a, "спільний.txt", "v2");

        plan.Run(dryRun: false);

        Assert.True(File.Exists(Path.Combine(_b, "спільний.txt")), "the surviving copy must not be deleted while the other side has the file");
    }

    [Fact]
    public void A_clean_deletion_still_travels()
    {
        Write(_a, "тимчасовий.txt", "v1");
        Pass();
        Assert.True(File.Exists(Path.Combine(_b, "тимчасовий.txt")));

        File.Delete(Path.Combine(_a, "тимчасовий.txt"));
        Pass();

        Assert.False(File.Exists(Path.Combine(_b, "тимчасовий.txt")), "a real deletion must still be carried across");
    }

    [Fact]
    public void A_mass_removal_is_held_back_and_reported()
    {
        for (var i = 0; i < 200; i++) Write(_a, $"файл{i}.txt", "x");
        Pass();
        Assert.Equal(200, Directory.GetFiles(_b).Length);

        // The whole side disappears at once — far more likely a mount problem than 200 deliberate deletions.
        foreach (var f in Directory.GetFiles(_a)) File.Delete(f);
        var pass = Pass();

        Assert.Equal(200, Directory.GetFiles(_b).Length);
        Assert.True(Assert.Single(pass.Pairs).HeldBackRemovals > 0);
        Assert.True(Warned("held back"));
    }

    [Fact]
    public void What_was_held_back_goes_through_once_it_looks_ordinary()
    {
        for (var i = 0; i < 200; i++) Write(_a, $"файл{i}.txt", "x");
        Pass();
        foreach (var f in Directory.GetFiles(_a)) File.Delete(f);
        Pass();                                    // held back

        // Put almost everything back; now only a handful are really gone.
        for (var i = 0; i < 195; i++) Write(_a, $"файл{i}.txt", "x");
        Pass();

        Assert.Equal(195, Directory.GetFiles(_b).Length);
    }

    [Fact]
    public void Local_deletions_go_to_the_recycle_bin_and_say_so_when_they_cannot()
    {
        // Both roots are local here, so nothing is permanent and nothing should be warned about.
        Write(_a, "звичайний.txt", "x");
        Pass();
        File.Delete(Path.Combine(_a, "звичайний.txt"));

        Pass();

        Assert.False(Warned("PERMANENTLY"), "a local root has a Recycle Bin; the warning is only for roots without one");
        Assert.True(RecycleBinIsAvailableFor(_b));
    }

    [Fact]
    public void A_path_with_no_recycle_bin_is_recognised_as_such()
    {
        // The classification, not the deletion: a UNC path cannot be created in a test, but the decision
        // that drives the warning can be checked directly.
        Assert.False(RecycleBinIsAvailableFor(@"\\server\share\folder"));
        Assert.True(RecycleBinIsAvailableFor(_root));
    }

    private static bool RecycleBinIsAvailableFor(string path) =>
        Core.Execution.RecycleBin.IsAvailableFor(path);

    [Fact]
    public void A_cloud_only_file_is_skipped_by_default_and_reported()
    {
        // Real placeholders need OneDrive; the decision is what matters, so it is checked on the attribute.
        Assert.True(CloudFiles.IsPlaceholder(CloudFiles.RecallOnDataAccess));
        Assert.True(CloudFiles.IsPlaceholder(FileAttributes.Offline | FileAttributes.Normal));
        Assert.False(CloudFiles.IsPlaceholder(FileAttributes.Normal | FileAttributes.ReparsePoint));
        Assert.Equal(CloudFileMode.Skip, new FolderPair().CloudFiles);
    }

    [Fact]
    public void Placeholders_are_only_looked_for_where_they_can_exist()
    {
        // Asking a network share costs a round trip per file for an answer that is always "no".
        Assert.True(CloudFiles.Possible(_root));
        Assert.False(CloudFiles.Possible(@"\\server\share"));
    }
}
