using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Logging;

namespace WorkFlowSync.Tests;

/// <summary>
/// Two-way mode on the real file system: additions, edits and deletions travel both ways
/// (docs/product/features/sync-modes.md).
/// </summary>
[Trait("Category", "E2E")]
public sealed class TwoWayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-2w-" + Guid.NewGuid().ToString("N"));
    private readonly string _a;
    private readonly string _b;
    private readonly string _configPath;
    private readonly SyncConfig _config;

    public TwoWayTests()
    {
        _a = Path.Combine(_root, "a");
        _b = Path.Combine(_root, "b");
        Directory.CreateDirectory(_a);
        Directory.CreateDirectory(_b);
        _configPath = Path.Combine(_root, "config.json");
        _config = new SyncConfig
        {
            Pairs = { new FolderPair { Name = "t", Source = _a, Target = _b, Mode = SyncMode.TwoWay } },
            ScanParallelism = 2,
        };
        ConfigFile.Save(_config, _configPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private PassResult Pass() => new SyncRunner(_config, _configPath, new MemorySyncLog()).Run(dryRun: false);

    private static void Write(string root, string rel, string text, DateTime? mtimeUtc = null)
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        if (mtimeUtc is { } m) File.SetLastWriteTimeUtc(p, m);
    }

    private static string Read(string root, string rel) => File.ReadAllText(Path.Combine(root, rel));
    private static bool Exists(string root, string rel) => File.Exists(Path.Combine(root, rel));

    [Fact]
    public void New_files_travel_in_both_directions()
    {
        Write(_a, @"one\from-a.txt", "A");
        Write(_b, @"two\from-b.txt", "B");

        Pass();

        Assert.Equal("A", Read(_b, @"one\from-a.txt"));
        Assert.Equal("B", Read(_a, @"two\from-b.txt"));
    }

    [Fact]
    public void Edit_on_either_side_is_carried_over()
    {
        Write(_a, "note.txt", "v1");
        Pass();

        // Edited on the mirror side: in mirror mode this would be frozen as local_modified.
        Write(_b, "note.txt", "v2", DateTime.UtcNow.AddSeconds(30));
        Pass();
        Assert.Equal("v2", Read(_a, "note.txt"));

        Write(_a, "note.txt", "v3", DateTime.UtcNow.AddMinutes(2));
        Pass();
        Assert.Equal("v3", Read(_b, "note.txt"));
    }

    [Fact]
    public void Deletion_on_one_side_removes_the_other_copy()
    {
        Write(_a, @"dir\gone.txt", "x");
        Pass();
        Assert.True(Exists(_b, @"dir\gone.txt"));

        File.Delete(Path.Combine(_b, @"dir\gone.txt"));
        Directory.Delete(Path.Combine(_b, "dir"));
        var pass = Pass();

        Assert.False(Exists(_a, @"dir\gone.txt"));
        Assert.False(Directory.Exists(Path.Combine(_a, "dir")));
        Assert.Equal(2, pass.Pairs[0].Plan!.Deleted);

        // And it stays deleted: nothing is resurrected on the next pass.
        Pass();
        Assert.False(Exists(_a, @"dir\gone.txt"));
        Assert.False(Exists(_b, @"dir\gone.txt"));
    }

    [Fact]
    public void Delete_on_one_side_and_edit_on_the_other_keeps_the_edit()
    {
        Write(_a, "both.txt", "v1");
        Pass();

        File.Delete(Path.Combine(_a, "both.txt"));
        Write(_b, "both.txt", "edited", DateTime.UtcNow.AddMinutes(1));
        var pass = Pass();

        Assert.Equal("edited", Read(_a, "both.txt"));
        Assert.Equal(1, pass.Pairs[0].Plan!.Conflicts);
    }

    [Fact]
    public void Changed_on_both_sides_the_newer_copy_wins()
    {
        Write(_a, "c.txt", "v1");
        Pass();

        Write(_a, "c.txt", "older", DateTime.UtcNow.AddMinutes(1));
        Write(_b, "c.txt", "newer", DateTime.UtcNow.AddMinutes(5));
        var pass = Pass();

        Assert.Equal("newer", Read(_a, "c.txt"));
        Assert.Equal("newer", Read(_b, "c.txt"));
        Assert.Equal(1, pass.Pairs[0].Plan!.Conflicts);
    }

    [Fact]
    public void Identical_trees_produce_no_work()
    {
        Write(_a, "same.txt", "x");
        Pass();
        var pass = Pass();

        Assert.Equal(0, pass.Pairs[0].Plan!.New);
        Assert.Equal(0, pass.Pairs[0].Plan!.Updated);
        Assert.Equal(0, pass.Pairs[0].Plan!.Deleted);
    }

    [Fact]
    public void A_folder_removed_on_one_side_survives_when_new_content_arrives_in_it()
    {
        Write(_a, @"keep\old.txt", "old");
        Pass();

        Directory.Delete(Path.Combine(_b, "keep"), recursive: true);
        Write(_a, @"keep\fresh.txt", "fresh");
        Pass();

        Assert.True(Exists(_b, @"keep\fresh.txt"));
        Assert.True(Directory.Exists(Path.Combine(_a, "keep")));
    }

    [Fact]
    public void Excludes_apply_to_both_sides()
    {
        _config.Pairs[0].Exclude.Add("*.tmp");
        Write(_a, "a.tmp", "x");
        Write(_b, "b.tmp", "y");

        Pass();

        Assert.False(Exists(_b, "a.tmp"));
        Assert.False(Exists(_a, "b.tmp"));
    }
}
