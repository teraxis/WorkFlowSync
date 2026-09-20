using System.Diagnostics;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Tests;

/// <summary>Real file system under %TEMP%: scanner + planner + executor + state, the three customer rules end to end.</summary>
[Trait("Category", "E2E")]
public sealed class EndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly string _src;
    private readonly string _dst;
    private readonly string _configPath;
    private readonly SyncConfig _config;

    public EndToEndTests()
    {
        _src = Path.Combine(_root, "src");
        _dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(_src);
        _configPath = Path.Combine(_root, "config.json");
        _config = new SyncConfig
        {
            Pairs = { new FolderPair { Name = "t", Source = _src, Target = _dst, Exclude = { "*.tmp" } } },
            StatePath = "state.db",
            LogPath = "logs",
            ScanParallelism = 4,
        };
        ConfigFile.Save(_config, _configPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* junctions/locks: best effort */ }
    }

    private PassResult Pass(bool dryRun = false, ISyncLog? log = null) =>
        new SyncRunner(_config, _configPath, log ?? new MemorySyncLog()).Run(dryRun);

    private void WriteSrc(string rel, string content, DateTime? mtime = null)
    {
        var p = Path.Combine(_src, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        if (mtime is { } m) File.SetLastWriteTimeUtc(p, m);
    }

    private string ReadDst(string rel) => File.ReadAllText(Path.Combine(_dst, rel));
    private bool DstExists(string rel) => File.Exists(Path.Combine(_dst, rel)) || Directory.Exists(Path.Combine(_dst, rel));

    private StateEntry State(string rel)
    {
        using var store = new StateStore(Path.Combine(_root, "state.db"));
        return store.Load("t")[rel];
    }

    [Fact]
    public void Rules_1_2_3_end_to_end()
    {
        WriteSrc(@"2026\звіт.docx", "v1", new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        WriteSrc(@"2026\junk.tmp", "x");
        WriteSrc(@"верх.txt", "top");

        // Rule 2: new things are copied; excludes honoured.
        var p1 = Pass();
        Assert.Equal(0, p1.Errors);
        Assert.Equal("v1", ReadDst(@"2026\звіт.docx"));
        Assert.Equal("top", ReadDst("верх.txt"));
        Assert.False(DstExists(@"2026\junk.tmp"));
        Assert.Equal(new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(_dst, @"2026\звіт.docx")));
        Assert.Equal(EntryStatus.Active, State(@"2026\звіт.docx").Status);

        // Unchanged second pass: nothing to do.
        var p2 = Pass();
        Assert.Equal(0, p2.Pairs[0].Execution!.FilesCopied + p2.Pairs[0].Execution!.FilesUpdated);

        // Rule 2 (update): source changed, local intact → updated.
        WriteSrc(@"2026\звіт.docx", "v2-longer", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        Pass();
        Assert.Equal("v2-longer", ReadDst(@"2026\звіт.docx"));

        // Rule 1: deleted in source → local copy stays.
        File.Delete(Path.Combine(_src, "верх.txt"));
        Pass();
        Assert.Equal("top", ReadDst("верх.txt"));
        Assert.Equal(EntryStatus.Active, State("верх.txt").Status);

        // Rule 3a: edited locally → never overwritten again.
        File.WriteAllText(Path.Combine(_dst, @"2026\звіт.docx"), "my local edit, much longer than the source");
        WriteSrc(@"2026\звіт.docx", "v3", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        Pass();
        Assert.Equal("my local edit, much longer than the source", ReadDst(@"2026\звіт.docx"));
        Assert.Equal(EntryStatus.LocalModified, State(@"2026\звіт.docx").Status);

        // Rule 3b: deleted locally → tombstone, never comes back even though the source still has it.
        WriteSrc(@"2026\other.txt", "o");
        Pass();
        Assert.True(DstExists(@"2026\other.txt"));
        File.Delete(Path.Combine(_dst, @"2026\other.txt"));
        Pass();
        Assert.False(DstExists(@"2026\other.txt"));
        Assert.Equal(EntryStatus.Tombstone, State(@"2026\other.txt").Status);
        Pass();
        Assert.False(DstExists(@"2026\other.txt"));

        // Rule 3c: folder moved away locally → cascade; new source files under it stay out.
        Directory.Delete(Path.Combine(_dst, "2026"), recursive: true);
        Pass();
        Assert.Equal(EntryStatus.Tombstone, State("2026").Status);
        WriteSrc(@"2026\late.txt", "late");
        Pass();
        Assert.False(DstExists(@"2026\late.txt"));
        Assert.False(DstExists("2026"));
    }

    [Fact]
    public void Dry_run_changes_nothing()
    {
        WriteSrc("a.txt", "a");
        var log = new MemorySyncLog();
        var p = Pass(dryRun: true, log: log);
        Assert.Equal(1, p.Pairs[0].Plan!.New);
        Assert.False(Directory.Exists(_dst));
        Assert.False(File.Exists(Path.Combine(_root, "state.db")) && new FileInfo(Path.Combine(_root, "state.db")).Length > 0 && StateHasRows());
        Assert.Contains(log.Lines, l => l.Message.Contains("[dry-run]"));
    }

    private bool StateHasRows()
    {
        using var store = new StateStore(Path.Combine(_root, "state.db"));
        return store.Count("t") > 0;
    }

    [Fact]
    public void Existing_mirror_is_adopted_not_recopied()
    {
        // Simulate what FreeFileSync left behind: identical file on both sides, unknown to the state.
        WriteSrc("same.txt", "same", new DateTime(2025, 5, 5, 0, 0, 0, DateTimeKind.Utc));
        Directory.CreateDirectory(_dst);
        File.WriteAllText(Path.Combine(_dst, "same.txt"), "same");
        File.SetLastWriteTimeUtc(Path.Combine(_dst, "same.txt"), new DateTime(2025, 5, 5, 0, 0, 0, DateTimeKind.Utc));

        var p = Pass();
        Assert.Equal(0, p.Pairs[0].Execution!.FilesCopied);
        Assert.Equal(1, p.Pairs[0].Plan!.Adopted);
        var e = State("same.txt");
        Assert.Equal(EntryStatus.Active, e.Status);
        Assert.Equal(4, e.CopiedSize);
        // Backfill on first pass: first_seen is not "now".
        Assert.True(e.FirstSeenUtc <= new DateTimeOffset(2025, 5, 5, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Hidden_and_system_files_are_mirrored()
    {
        WriteSrc("hidden.txt", "h");
        File.SetAttributes(Path.Combine(_src, "hidden.txt"), FileAttributes.Hidden | FileAttributes.System);
        Pass();
        Assert.Equal("h", ReadDst("hidden.txt"));
    }

    [Fact]
    public void Unavailable_source_skips_the_pair_without_touching_state()
    {
        WriteSrc("a.txt", "a");
        Pass();
        var before = State("a.txt");

        _config.Pairs[0].Source = Path.Combine(_root, "does-not-exist");
        var p = Pass();
        Assert.True(p.Pairs[0].Skipped);
        Assert.Equal(before, State("a.txt"));
        Assert.Equal("a", ReadDst("a.txt"));
    }

    [Fact]
    public void Auto_clean_recycles_expired_files_and_empty_folders_and_never_brings_them_back()
    {
        var old = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        WriteSrc(@"2024\old.txt", "old", old);
        File.SetCreationTimeUtc(Path.Combine(_src, @"2024\old.txt"), old);
        WriteSrc(@"2026\new.txt", "new");
        _config.Pairs[0].AutoClean = TimeSpan.FromDays(365);

        // Auto-clean on its own is exactly that — a cleaner. It works on what we already hold, so old.txt is
        // copied on this pass (new to us) and recycled on the next, once it is an active row. Not wanting it
        // copied in the first place is the intake window's job, and it is deliberately not set here.
        Pass();
        Assert.Equal("old", ReadDst(@"2024\old.txt"));
        Assert.True(State(@"2024\old.txt").FirstSeenUtc <= new DateTimeOffset(old));

        var p2 = Pass();
        Assert.Equal(1, p2.Pairs[0].Execution!.FilesRecycled);
        Assert.Equal(1, p2.Pairs[0].Execution!.DirectoriesRecycled);
        Assert.False(DstExists(@"2024\old.txt"));
        Assert.False(DstExists("2024"));
        Assert.Equal("new", ReadDst(@"2026\new.txt"));
        Assert.Equal(EntryStatus.Tombstone, State(@"2024\old.txt").Status);
        using (var store = new StateStore(Path.Combine(_root, "state.db")))
            Assert.False(store.Load("t").ContainsKey("2024"));           // folder forgotten, not tombstoned

        // Source still has old.txt → never returns; a NEW file in the old folder does arrive (folder comes back).
        WriteSrc(@"2024\addendum.txt", "late");
        var p3 = Pass();
        Assert.False(DstExists(@"2024\old.txt"));
        Assert.Equal("late", ReadDst(@"2024\addendum.txt"));
        Assert.Equal(0, p3.Pairs[0].Execution!.FilesRecycled);
    }

    /// <summary>
    /// The intake window on a real tree: an old archive is never fetched over the network, the source keeps
    /// every byte of it, and the local folder is only ever added to. This is the case that used to copy the
    /// whole archive and bin it one pass later.
    /// </summary>
    [Fact]
    public void Intake_window_never_copies_an_old_archive_and_removes_nothing()
    {
        var old = new DateTime(2019, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        WriteSrc(@"Архів 2019\звіт.txt", "old", old);
        File.SetCreationTimeUtc(Path.Combine(_src, @"Архів 2019\звіт.txt"), old);
        WriteSrc("свіже.txt", "new");
        _config.Pairs[0].MaxAge = TimeSpan.FromDays(30);

        var p1 = Pass();

        Assert.Equal("new", ReadDst("свіже.txt"));
        Assert.False(DstExists(@"Архів 2019\звіт.txt"));
        Assert.False(DstExists("Архів 2019"));                        // not even an empty folder for it
        Assert.Equal(0, p1.Pairs[0].Execution!.FilesRecycled);        // a filter removes nothing, ever
        Assert.Equal(1, p1.Pairs[0].Plan!.TooOld);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_src, @"Архів 2019\звіт.txt")));   // source untouched

        // It stays out on every later pass, and editing it in the source changes nothing here.
        WriteSrc(@"Архів 2019\звіт.txt", "edited", old);
        var p2 = Pass();
        Assert.False(DstExists(@"Архів 2019\звіт.txt"));
        Assert.Equal(0, p2.Pairs[0].Execution!.FilesCopied);

        // A file put there TODAY is new to us, however old the folder around it is — and it arrives.
        WriteSrc(@"Архів 2019\доповнення.txt", "late");
        Pass();
        Assert.Equal("late", ReadDst(@"Архів 2019\доповнення.txt"));
        Assert.False(DstExists(@"Архів 2019\звіт.txt"));
    }

    [Fact]
    public void Junction_is_followed_once_and_cycles_are_skipped()
    {
        WriteSrc(@"real\inner.txt", "inner");
        // Junction into the tree (no admin needed) and a junction pointing back at the root (cycle).
        if (!Mklink(Path.Combine(_src, "link"), Path.Combine(_src, "real")) || !Mklink(Path.Combine(_src, "real", "back"), _src))
            return; // mklink unavailable in this environment — nothing to verify

        var log = new MemorySyncLog();
        var p = Pass(log: log);

        Assert.Equal("inner", ReadDst(@"real\inner.txt"));
        Assert.Equal("inner", ReadDst(@"link\inner.txt"));            // followed, materialised as a real folder
        Assert.False(Directory.Exists(Path.Combine(_dst, "real", "back")) && Directory.Exists(Path.Combine(_dst, "real", "back", "real")));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warn && l.Message.Contains("cycle"));
        Assert.True(p.Pairs[0].SourceScan!.LinkCount >= 1);
        Assert.True(State(@"link\inner.txt").ViaLink);
        Assert.False(new DirectoryInfo(Path.Combine(_dst, "link")).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    private static bool Mklink(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(10000);
            return proc.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }
}
