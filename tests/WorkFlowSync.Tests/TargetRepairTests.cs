using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Scanning;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Tests;

public sealed class TargetRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-repair-" + Guid.NewGuid().ToString("N"));

    public TargetRepairTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Unmappable_file_becomes_a_plan_conflict_without_hiding_safe_work()
    {
        var targetRoot = @"C:\" + new string('R', 225);
        var safe = "safe.txt";
        var impossible = new string('L', 80) + ".xlsx";
        var source = new Dictionary<string, ScanEntry>
        {
            [safe] = new() { RelativePath = safe, Kind = EntryKind.File },
            [impossible] = new() { RelativePath = impossible, Kind = EntryKind.File },
        };

        var plan = TargetRepair.Build("pair", targetRoot, 240, source,
            new Dictionary<string, ScanEntry>(), new Dictionary<string, StateEntry>());

        Assert.Equal(1, plan.Conflicts);
        Assert.Contains(plan.Rows, row => row.Action == "conflict" && row.OldPath == impossible);
        Assert.Equal(1, plan.CheckedPaths);
        Assert.Equal(0, plan.TooLongPaths);
        Assert.Equal(0, plan.ShortHashes);
    }

    [Fact]
    public void Untracked_legacy_hash_copy_is_recycled_after_tracked_copy_moves()
    {
        var original = new string('C', 100) + ".txt";
        var legacy = "CCC_#12345678.txt";
        var now = DateTimeOffset.UtcNow;
        var source = new Dictionary<string, ScanEntry>
        {
            [original] = new()
            {
                RelativePath = original,
                Kind = EntryKind.File,
                Size = 5,
                MtimeUtc = now
            }
        };
        var target = new Dictionary<string, ScanEntry>
        {
            [original] = new()
            {
                RelativePath = original,
                Kind = EntryKind.File,
                Size = 5,
                MtimeUtc = now
            },
            [legacy] = new()
            {
                RelativePath = legacy,
                Kind = EntryKind.File,
                Size = 5,
                MtimeUtc = now
            },
        };
        var state = new Dictionary<string, StateEntry>
        {
            [original] = new()
            {
                PairName = "pair",
                RelativePath = original,
                TargetPath = original,
                Kind = EntryKind.File,
                FirstSeenUtc = now,
                CopiedSize = 5,
                CopiedMtimeUtc = now
            }
        };

        var plan = TargetRepair.Build("pair", _root, _root.Length + 40, source, target, state);
        Assert.Single(plan.Rows, r => r.Action == "move");
        Assert.Single(plan.Rows, r => r.Action == "recycle-duplicate-untracked");
        Assert.Equal(0, plan.Conflicts);
        Assert.Equal(1, plan.RecycleFiles);
    }

    [Fact]
    public void Fifty_four_files_reuse_existing_full_copies_and_recycle_old_hash_variants()
    {
        var targetRoot = @"C:\" + new string('R', 62);
        var ancestor = string.Join("\\", new string('A', 17), new string('B', 70),
            new string('C', 19), new string('D', 19));
        var fullDir = ancestor + "\\" + new string('E', 56);
        var oldDir = ancestor + "\\EEE_#123456";
        var now = DateTimeOffset.UtcNow;
        var source = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        var target = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [fullDir] = new() { RelativePath = fullDir, Kind = EntryKind.Directory },
            [oldDir] = new() { RelativePath = oldDir, Kind = EntryKind.Directory },
        };
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 54; i++)
        {
            var name = new string('F', 41) + $"_{2017 + i / 12:D4}_{i % 12 + 1:D2}.xlsx";
            var original = fullDir + "\\" + name;
            var legacy = oldDir + "\\FFF_#" + i.ToString("x8") + ".xlsx";
            source[original] = new ScanEntry
            {
                RelativePath = original,
                Kind = EntryKind.File,
                Size = 1000 + i,
                MtimeUtc = now
            };
            target[original] = new ScanEntry
            {
                RelativePath = original,
                Kind = EntryKind.File,
                Size = 1000 + i,
                MtimeUtc = now
            };
            target[legacy] = new ScanEntry
            {
                RelativePath = legacy,
                Kind = EntryKind.File,
                Size = 1000 + i,
                MtimeUtc = now
            };
            state[original] = new StateEntry
            {
                PairName = "pair",
                RelativePath = original,
                TargetPath = original,
                Kind = EntryKind.File,
                FirstSeenUtc = now,
                CopiedSize = 1000 + i,
                CopiedMtimeUtc = now
            };
        }

        var plan = TargetRepair.Build("pair", targetRoot, 240, source, target, state);
        Assert.Equal(54, plan.RenameFiles);
        Assert.Equal(54, plan.RecycleFiles);
        Assert.Equal(2, plan.Rows.Count(r => r.Action == "recycle-empty-directory"));
        Assert.Equal(0, plan.Conflicts);
        Assert.Equal(0, plan.TooLongPaths);
        Assert.Equal(0, plan.ShortHashes);
    }

    [Fact]
    public void Ambiguous_legacy_copy_is_reported_without_recycling()
    {
        var now = DateTimeOffset.UtcNow;
        var names = new[] { new string('A', 100) + "1.txt", new string('A', 100) + "2.txt" };
        var source = names.ToDictionary(n => n, n => new ScanEntry
        {
            RelativePath = n,
            Kind = EntryKind.File,
            Size = 7,
            MtimeUtc = now
        });
        var legacy = "AAA_#12345678.txt";
        var target = new Dictionary<string, ScanEntry>
        {
            [legacy] = new()
            {
                RelativePath = legacy,
                Kind = EntryKind.File,
                Size = 7,
                MtimeUtc = now
            }
        };

        var plan = TargetRepair.Build("pair", _root, _root.Length + 40, source, target,
            new Dictionary<string, StateEntry>());

        Assert.Contains(plan.Rows, r => r.Action == "conflict" && r.OldPath == legacy);
        Assert.Equal(0, plan.RecycleFiles);
    }

    [Fact]
    public void Dry_run_plan_does_not_change_target_or_state_and_apply_moves_without_copying()
    {
        var targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetRoot);
        var original = new string('A', 100) + ".txt";
        var oldFile = Path.Combine(targetRoot, original);
        File.WriteAllText(oldFile, "seven");
        var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(oldFile), TimeSpan.Zero);
        var source = new Dictionary<string, ScanEntry>
        {
            [original] = new() { RelativePath = original, Kind = EntryKind.File, Size = 5, MtimeUtc = mtime }
        };
        var target = new Dictionary<string, ScanEntry>
        {
            [original] = new() { RelativePath = original, Kind = EntryKind.File, Size = 5, MtimeUtc = mtime }
        };
        using var store = new StateStore(Path.Combine(_root, "state.db"));
        store.Upsert(new[] { new StateEntry
        {
            PairName = "pair", RelativePath = original, TargetPath = original,
            Kind = EntryKind.File, FirstSeenUtc = mtime, LastSeenUtc = mtime,
            CopiedSize = 5, CopiedMtimeUtc = mtime,
        } });

        var plan = TargetRepair.Build("pair", targetRoot, targetRoot.Length + 40, source, target, store.Load("pair"));
        var row = Assert.Single(plan.Rows);
        Assert.Equal("move", row.Action);
        Assert.True(File.Exists(oldFile));
        Assert.Equal(original, store.Load("pair")[original].TargetPath);

        TargetRepair.Execute(plan, store);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(Path.Combine(targetRoot, row.NewPath)));
        var relocated = Assert.Single(store.Load("pair"));
        Assert.Equal(row.NewPath, relocated.Value.TargetPath);
        Assert.Equal(original, relocated.Value.RemoteId);
        Assert.Equal(mtime.ToUnixTimeMilliseconds(), relocated.Value.FirstSeenUtc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Different_destination_metadata_is_reported_and_never_scheduled_for_removal()
    {
        var original = new string('B', 100) + ".txt";
        var canonical = PathShortener.MapEntries(
            new Dictionary<string, ScanEntry> { [original] = new() { RelativePath = original, Kind = EntryKind.File } },
            _root, true, _root.Length + 40, null, null, out _).Keys.Single();
        var now = DateTimeOffset.UtcNow;
        var source = new Dictionary<string, ScanEntry>
        {
            [original] = new() { RelativePath = original, Kind = EntryKind.File }
        };
        var target = new Dictionary<string, ScanEntry>
        {
            [original] = new() { RelativePath = original, Kind = EntryKind.File, Size = 5, MtimeUtc = now },
            [canonical] = new() { RelativePath = canonical, Kind = EntryKind.File, Size = 8, MtimeUtc = now },
        };
        var state = new Dictionary<string, StateEntry>
        {
            [original] = new()
            {
                PairName = "pair",
                RelativePath = original,
                Kind = EntryKind.File,
                FirstSeenUtc = now,
                CopiedSize = 5,
                CopiedMtimeUtc = now
            }
        };

        var plan = TargetRepair.Build("pair", _root, _root.Length + 40, source, target, state);
        Assert.Contains(plan.Rows, r => r.Action == "conflict" && r.OldPath == original);
        Assert.Equal(0, plan.ActionCount);
    }

    [Fact]
    public void Two_legacy_copies_of_one_source_do_not_schedule_two_moves_to_one_destination()
    {
        var original = new string('C', 100) + ".txt";
        var variant = "old_#12345678.txt";
        var now = DateTimeOffset.UtcNow;
        var source = new Dictionary<string, ScanEntry>
        {
            [original] = new() { RelativePath = original, Kind = EntryKind.File }
        };
        var target = new Dictionary<string, ScanEntry>
        {
            [original] = new() { RelativePath = original, Kind = EntryKind.File, Size = 5, MtimeUtc = now },
            [variant] = new() { RelativePath = variant, Kind = EntryKind.File, Size = 5, MtimeUtc = now },
        };
        var state = new Dictionary<string, StateEntry>
        {
            [original] = new()
            {
                PairName = "pair",
                RelativePath = original,
                Kind = EntryKind.File,
                FirstSeenUtc = now,
                CopiedSize = 5,
                CopiedMtimeUtc = now
            },
            [variant] = new()
            {
                PairName = "pair",
                RelativePath = variant,
                RemoteId = original,
                Kind = EntryKind.File,
                FirstSeenUtc = now,
                CopiedSize = 5,
                CopiedMtimeUtc = now
            },
        };

        var plan = TargetRepair.Build("pair", _root, _root.Length + 40, source, target, state);
        Assert.Equal(2, plan.ActionCount);
        Assert.Single(plan.Rows, r => r.Action == "move");
        Assert.Single(plan.Rows, r => r.Action == "recycle-duplicate-state");
        Assert.Equal(0, plan.Conflicts);
    }

    [Fact]
    public void Apply_moves_a_legacy_path_longer_than_windows_max_path()
    {
        var targetRoot = Path.Combine(_root, "long-target");
        var first = new string('P', 80);
        var second = Path.Combine(first, new string('Q', 80));
        var original = Path.Combine(second, new string('R', 90) + ".txt");
        var oldFull = Path.Combine(targetRoot, original);
        Directory.CreateDirectory(Path.GetDirectoryName(oldFull)!);
        File.WriteAllText(@"\\?\" + oldFull, "long");
        Assert.True(oldFull.Length > 259);
        var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(@"\\?\" + oldFull), TimeSpan.Zero);
        var source = new Dictionary<string, ScanEntry>
        {
            [first] = new() { RelativePath = first, Kind = EntryKind.Directory },
            [second] = new() { RelativePath = second, Kind = EntryKind.Directory },
            [original] = new() { RelativePath = original, Kind = EntryKind.File, Size = 4, MtimeUtc = mtime },
        };
        var target = new Dictionary<string, ScanEntry>
        {
            [original] = new() { RelativePath = original, Kind = EntryKind.File, Size = 4, MtimeUtc = mtime }
        };
        using var store = new StateStore(Path.Combine(_root, "long-state.db"));
        store.Upsert(new[] { new StateEntry
        {
            PairName = "pair", RelativePath = original, Kind = EntryKind.File,
            FirstSeenUtc = mtime, CopiedSize = 4, CopiedMtimeUtc = mtime,
        } });
        var plan = TargetRepair.Build("pair", targetRoot, 200, source, target, store.Load("pair"));
        var move = Assert.Single(plan.Rows, r => r.Action == "move");
        TargetRepair.Execute(plan, store);
        Assert.False(File.Exists(@"\\?\" + oldFull));
        Assert.True(File.Exists(Path.Combine(targetRoot, move.NewPath)));
    }

    [Fact]
    public void Tombstone_is_revived_to_active_and_relocated_if_physically_present()
    {
        var original = "VeryLongFolder_#1234\\test_doc.docx";
        var now = DateTimeOffset.UtcNow;
        var source = new Dictionary<string, ScanEntry>
        {
            [original] = new() { RelativePath = original, Kind = EntryKind.File, Size = 10, MtimeUtc = now }
        };
        var target = new Dictionary<string, ScanEntry>
        {
            [original] = new() { RelativePath = original, Kind = EntryKind.File, Size = 10, MtimeUtc = now }
        };
        var targetRoot = Path.Combine(_root, "revive-target");
        Directory.CreateDirectory(targetRoot);
        var oldFull = Path.Combine(targetRoot, original);
        Directory.CreateDirectory(Path.GetDirectoryName(oldFull)!);
        File.WriteAllText(oldFull, "0123456789");
        File.SetLastWriteTimeUtc(oldFull, now.UtcDateTime);

        using var store = new StateStore(Path.Combine(_root, "revive-state.db"));
        store.Upsert(new[] { new StateEntry
        {
            PairName = "pair", RelativePath = original, TargetPath = original,
            Status = EntryStatus.Tombstone, Kind = EntryKind.File,
            FirstSeenUtc = now, LastSeenUtc = now,
            CopiedSize = 10, CopiedMtimeUtc = now
        } });

        var plan = TargetRepair.Build("pair", targetRoot, 200, source, target, store.Load("pair"));
        Assert.Contains(plan.Rows, r => r.Action is "move" or "adopt-existing");

        TargetRepair.Execute(plan, store);
        var state = store.Load("pair");
        var active = Assert.Single(state.Values);
        Assert.Equal(EntryStatus.Active, active.Status);
    }

    [Fact]
    public void Empty_variant_folder_is_scheduled_for_recycle_and_not_untracked()
    {
        var targetRoot = Path.Combine(_root, "empty-target");
        Directory.CreateDirectory(targetRoot);
        var variantDir = "LongFolder_#123456";
        var source = new Dictionary<string, ScanEntry>();
        var target = new Dictionary<string, ScanEntry>
        {
            [variantDir] = new() { RelativePath = variantDir, Kind = EntryKind.Directory }
        };
        var state = new Dictionary<string, StateEntry>();

        var plan = TargetRepair.Build("pair", targetRoot, 200, source, target, state);
        var row = Assert.Single(plan.Rows);
        Assert.Equal("recycle-empty-directory", row.Action);
        Assert.Equal(0, plan.Conflicts);
    }

    [Fact]
    public void Cloud_duplicate_parenthesized_copy_is_recycled()
    {
        var targetRoot = Path.Combine(_root, "cloud-dup-target");
        Directory.CreateDirectory(targetRoot);
        var orig = "doc_#1234.pdf";
        var dup = "doc_#1234 (1).pdf";
        var now = DateTimeOffset.UtcNow;
        var source = new Dictionary<string, ScanEntry>();
        var target = new Dictionary<string, ScanEntry>
        {
            [orig] = new() { RelativePath = orig, Kind = EntryKind.File, Size = 20, MtimeUtc = now },
            [dup] = new() { RelativePath = dup, Kind = EntryKind.File, Size = 20, MtimeUtc = now }
        };
        var state = new Dictionary<string, StateEntry>
        {
            [orig] = new() { PairName = "pair", RelativePath = orig, Kind = EntryKind.File, FirstSeenUtc = now, CopiedSize = 20, CopiedMtimeUtc = now }
        };

        var plan = TargetRepair.Build("pair", targetRoot, 200, source, target, state);
        Assert.Contains(plan.Rows, r => r.Action == "recycle-cloud-duplicate" && r.OldPath == dup);
        Assert.Equal(0, plan.Conflicts);
    }
}
