using WorkFlowSync.App.Views;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Scanning;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Tests;

public sealed class LegacyHashRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-legacy-" + Guid.NewGuid().ToString("N"));

    public LegacyHashRecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void HasLegacyHash_correctly_identifies_hashed_segments()
    {
        Assert.True(TargetRepair.HasLegacyHash("file_#12345678.pdf", "Original Long File Name.pdf"));
        Assert.True(TargetRepair.HasLegacyHash(@"folder_#12345678\file.pdf", @"Long Folder Name\file.pdf"));
        Assert.True(TargetRepair.HasLegacyHash(@"folder_#12345678\sub_#abcdef12\file_#98765432.pdf", @"A\B\C.pdf"));

        // No hash tag
        Assert.False(TargetRepair.HasLegacyHash("file.pdf", "file.pdf"));
        Assert.False(TargetRepair.HasLegacyHash(@"folder\file.pdf", @"folder\file.pdf"));

        // Legitimate source name with identical hash tag in source is not artificial shortening
        Assert.False(TargetRepair.HasLegacyHash("doc_#12345678.txt", "doc_#12345678.txt"));
        Assert.False(TargetRepair.HasLegacyHash(@"folder\doc_#12345678.txt", @"folder\doc_#12345678.txt"));

        // Null / whitespace
        Assert.False(TargetRepair.HasLegacyHash("", ""));
        Assert.False(TargetRepair.HasLegacyHash("   ", ""));
    }

    [Fact]
    public void Legacy_hash_recovery_plans_move_to_readable_name_and_preserves_first_seen()
    {
        var targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetRoot);

        var original = new string('С', 70) + ".pdf";
        var legacy = "ССС_#12345678.pdf";
        var oldFile = Path.Combine(targetRoot, legacy);
        File.WriteAllText(oldFile, "hello");
        var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(oldFile), TimeSpan.Zero);
        var firstSeen = mtime.AddDays(-10);

        var source = new Dictionary<string, ScanEntry>
        {
            [original] = new() { RelativePath = original, Kind = EntryKind.File, Size = 5, MtimeUtc = mtime }
        };
        var target = new Dictionary<string, ScanEntry>
        {
            [legacy] = new() { RelativePath = legacy, Kind = EntryKind.File, Size = 5, MtimeUtc = mtime }
        };

        using var store = new StateStore(Path.Combine(_root, "state.db"));
        store.Upsert(new[]
        {
            new StateEntry
            {
                PairName = "pair",
                RelativePath = legacy,
                TargetPath = legacy,
                RemoteId = original,
                Kind = EntryKind.File,
                Status = EntryStatus.Active,
                FirstSeenUtc = firstSeen,
                LastSeenUtc = mtime,
                CopiedSize = 5,
                CopiedMtimeUtc = mtime,
            }
        });

        // 1. Build plan
        var plan = TargetRepair.BuildLegacyRecovery("pair", targetRoot, targetRoot.Length + 40, source, target, store.Load("pair"));
        var row = Assert.Single(plan.Rows);
        Assert.Equal("move", row.Action);
        Assert.Equal(legacy, row.OldPath);
        Assert.DoesNotContain("_#", row.NewPath);
        Assert.EndsWith(".pdf", row.NewPath);
        Assert.True(Path.Combine(targetRoot, row.NewPath).Length <= targetRoot.Length + 40);

        // Dry-run check: files and db not changed yet
        Assert.True(File.Exists(oldFile));
        Assert.Equal(legacy, store.Load("pair")[legacy].TargetPath);

        // 2. Execute legacy recovery
        TargetRepair.ExecuteLegacyRecovery(plan, store);

        // Target file moved, old file gone
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(Path.Combine(targetRoot, row.NewPath)));

        // State row relocated: target_path updated, remote_id and first_seen preserved
        var loaded = store.Load("pair");
        var entry = Assert.Single(loaded.Values);
        Assert.Equal(row.NewPath, entry.TargetPath);
        Assert.Equal(original, entry.RemoteId);
        Assert.True(Math.Abs((firstSeen - entry.FirstSeenUtc).TotalMilliseconds) < 1);
        Assert.Equal(EntryStatus.Active, entry.Status);

        // 3. Repeat run: already readable, no moves planned!
        var freshTarget = new Dictionary<string, ScanEntry>
        {
            [row.NewPath] = new() { RelativePath = row.NewPath, Kind = EntryKind.File, Size = 5, MtimeUtc = mtime }
        };
        var plan2 = TargetRepair.BuildLegacyRecovery("pair", targetRoot, targetRoot.Length + 40, source, freshTarget, store.Load("pair"));
        Assert.Empty(plan2.Rows);
        Assert.Equal(0, plan2.ActionCount);
    }

    [Fact]
    public void Legacy_hash_recovery_handles_nested_directories_and_collisions()
    {
        var targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetRoot);

        var longDir = "A Very Long Project Folder Name For Testing";
        var legacyDir = "A Very_#12345678";
        var fileA = Path.Combine(longDir, "Document Collision Long Name.pdf");
        var fileB = Path.Combine(longDir, "Document Collision Other Name.pdf");

        var legacyFileA = Path.Combine(legacyDir, "Doc_#aaaaaaaa.pdf");
        var legacyFileB = Path.Combine(legacyDir, "Doc_#bbbbbbbb.pdf");

        Directory.CreateDirectory(Path.Combine(targetRoot, legacyDir));
        File.WriteAllText(Path.Combine(targetRoot, legacyFileA), "fileA");
        File.WriteAllText(Path.Combine(targetRoot, legacyFileB), "fileB");

        var now = DateTimeOffset.UtcNow;
        var source = new Dictionary<string, ScanEntry>
        {
            [longDir] = new() { RelativePath = longDir, Kind = EntryKind.Directory },
            [fileA] = new() { RelativePath = fileA, Kind = EntryKind.File, Size = 5, MtimeUtc = now },
            [fileB] = new() { RelativePath = fileB, Kind = EntryKind.File, Size = 5, MtimeUtc = now }
        };
        var target = new Dictionary<string, ScanEntry>
        {
            [legacyDir] = new() { RelativePath = legacyDir, Kind = EntryKind.Directory },
            [legacyFileA] = new() { RelativePath = legacyFileA, Kind = EntryKind.File, Size = 5, MtimeUtc = now },
            [legacyFileB] = new() { RelativePath = legacyFileB, Kind = EntryKind.File, Size = 5, MtimeUtc = now }
        };

        using var store = new StateStore(Path.Combine(_root, "state.db"));
        store.Upsert(new[]
        {
            new StateEntry
            {
                PairName = "pair", RelativePath = legacyDir, TargetPath = legacyDir, RemoteId = longDir,
                Kind = EntryKind.Directory, Status = EntryStatus.Active, FirstSeenUtc = now, LastSeenUtc = now
            },
            new StateEntry
            {
                PairName = "pair", RelativePath = legacyFileA, TargetPath = legacyFileA, RemoteId = fileA,
                Kind = EntryKind.File, Status = EntryStatus.Active, FirstSeenUtc = now, LastSeenUtc = now,
                CopiedSize = 5, CopiedMtimeUtc = now
            },
            new StateEntry
            {
                PairName = "pair", RelativePath = legacyFileB, TargetPath = legacyFileB, RemoteId = fileB,
                Kind = EntryKind.File, Status = EntryStatus.Active, FirstSeenUtc = now, LastSeenUtc = now,
                CopiedSize = 5, CopiedMtimeUtc = now
            }
        });

        // Set path limit to force shortening
        var limit = targetRoot.Length + 45;
        var plan = TargetRepair.BuildLegacyRecovery("pair", targetRoot, limit, source, target, store.Load("pair"));

        Assert.Equal(3, plan.Rows.Count);
        Assert.All(plan.Rows, r => Assert.Equal("move", r.Action));
        Assert.All(plan.Rows, r => Assert.DoesNotContain("_#", r.NewPath));

        // One of the files gets numeric suffix "(2)" because of stem collision under shortened limit
        var fileMoves = plan.Rows.Where(r => r.OldPath.EndsWith(".pdf")).ToList();
        Assert.Equal(2, fileMoves.Count);

        // Execute recovery
        TargetRepair.ExecuteLegacyRecovery(plan, store);

        // Verify files moved and contents intact
        foreach (var move in fileMoves)
        {
            var destPath = Path.Combine(targetRoot, move.NewPath);
            Assert.True(File.Exists(destPath), $"Expected destination file exists: {destPath}");
            Assert.False(File.Exists(Path.Combine(targetRoot, move.OldPath)), $"Old file should be gone: {move.OldPath}");
        }

        // Old legacy directory was empty, so it was cleaned up
        Assert.False(Directory.Exists(Path.Combine(targetRoot, legacyDir)));

        // State reflects new paths
        var stateAfter = store.Load("pair");
        Assert.Equal(3, stateAfter.Count);
        Assert.All(stateAfter.Values, e => Assert.DoesNotContain("_#", e.TargetPath));
    }

    [Fact]
    public void Legacy_hash_recovery_skips_tombstones_and_marks_local_modified_as_conflict()
    {
        var targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetRoot);

        var originalDeleted = "Deleted File Long Name.pdf";
        var legacyDeleted = "Deleted_#11111111.pdf";
        var originalMod = "Modified File Long Name.pdf";
        var legacyMod = "Modified_#22222222.pdf";

        var modPath = Path.Combine(targetRoot, legacyMod);
        File.WriteAllText(modPath, "modified-content");
        var now = DateTimeOffset.UtcNow;

        var source = new Dictionary<string, ScanEntry>
        {
            [originalMod] = new() { RelativePath = originalMod, Kind = EntryKind.File, Size = 16, MtimeUtc = now }
        };
        var target = new Dictionary<string, ScanEntry>
        {
            [legacyMod] = new() { RelativePath = legacyMod, Kind = EntryKind.File, Size = 16, MtimeUtc = now }
        };

        using var store = new StateStore(Path.Combine(_root, "state.db"));
        store.Upsert(new[]
        {
            // Tombstone
            new StateEntry
            {
                PairName = "pair", RelativePath = legacyDeleted, TargetPath = legacyDeleted, RemoteId = originalDeleted,
                Kind = EntryKind.File, Status = EntryStatus.Tombstone, FirstSeenUtc = now.AddDays(-5), LastSeenUtc = now
            },
            // LocalModified
            new StateEntry
            {
                PairName = "pair", RelativePath = legacyMod, TargetPath = legacyMod, RemoteId = originalMod,
                Kind = EntryKind.File, Status = EntryStatus.LocalModified, FirstSeenUtc = now.AddDays(-2), LastSeenUtc = now,
                CopiedSize = 10, CopiedMtimeUtc = now.AddDays(-2)
            }
        });

        var plan = TargetRepair.BuildLegacyRecovery("pair", targetRoot, targetRoot.Length + 40, source, target, store.Load("pair"));

        // Tombstone is not planned at all (remains final)
        Assert.DoesNotContain(plan.Rows, r => r.OldPath == legacyDeleted);

        // LocalModified is marked as conflict, not moved
        var modRow = Assert.Single(plan.Rows, r => r.OldPath == legacyMod);
        Assert.Equal("conflict", modRow.Action);
        Assert.Equal(0, plan.ActionCount);
        Assert.Equal(1, plan.Conflicts);
    }

    [Fact]
    public void Legacy_hash_recovery_handles_conflicts_safely()
    {
        var targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetRoot);

        var originalA = "File Missing On Target Disk.txt";
        var legacyA = "FileA_#11111111.txt";

        var originalB = "File With Changed Metadata On Disk.txt";
        var legacyB = "FileB_#22222222.txt";
        var fileBPath = Path.Combine(targetRoot, legacyB);
        File.WriteAllText(fileBPath, "changed-size-now");

        var originalC = "File Destination Already Occupied.txt";
        var legacyC = "FileC_#33333333.txt";
        File.WriteAllText(Path.Combine(targetRoot, legacyC), "contentC");

        var now = DateTimeOffset.UtcNow;

        var source = new Dictionary<string, ScanEntry>
        {
            [originalA] = new() { RelativePath = originalA, Kind = EntryKind.File, Size = 5, MtimeUtc = now },
            [originalB] = new() { RelativePath = originalB, Kind = EntryKind.File, Size = 5, MtimeUtc = now },
            [originalC] = new() { RelativePath = originalC, Kind = EntryKind.File, Size = 8, MtimeUtc = now }
        };

        // Determine what destination originalC would get:
        var canonical = PathShortener.MapEntries(source, targetRoot, true, targetRoot.Length + 40, null, null, out var owners);
        var canonC = canonical.FirstOrDefault(kvp => kvp.Value.RelativePath == originalC).Key ?? originalC;

        // Create an obstacle file at the destination of originalC
        Directory.CreateDirectory(Path.Combine(targetRoot, Path.GetDirectoryName(canonC) ?? ""));
        File.WriteAllText(Path.Combine(targetRoot, canonC), "obstacle");

        var target = new Dictionary<string, ScanEntry>
        {
            [legacyB] = new() { RelativePath = legacyB, Kind = EntryKind.File, Size = 16, MtimeUtc = now },
            [legacyC] = new() { RelativePath = legacyC, Kind = EntryKind.File, Size = 8, MtimeUtc = now },
            [canonC] = new() { RelativePath = canonC, Kind = EntryKind.File, Size = 8, MtimeUtc = now }
        };

        using var store = new StateStore(Path.Combine(_root, "state.db"));
        store.Upsert(new[]
        {
            new StateEntry
            {
                PairName = "pair", RelativePath = legacyA, TargetPath = legacyA, RemoteId = originalA,
                Kind = EntryKind.File, Status = EntryStatus.Active, FirstSeenUtc = now, LastSeenUtc = now,
                CopiedSize = 5, CopiedMtimeUtc = now
            },
            new StateEntry
            {
                PairName = "pair", RelativePath = legacyB, TargetPath = legacyB, RemoteId = originalB,
                Kind = EntryKind.File, Status = EntryStatus.Active, FirstSeenUtc = now, LastSeenUtc = now,
                CopiedSize = 5, CopiedMtimeUtc = now // state says 5, target disk has 16
            },
            new StateEntry
            {
                PairName = "pair", RelativePath = legacyC, TargetPath = legacyC, RemoteId = originalC,
                Kind = EntryKind.File, Status = EntryStatus.Active, FirstSeenUtc = now, LastSeenUtc = now,
                CopiedSize = 8, CopiedMtimeUtc = now
            }
        });

        var plan = TargetRepair.BuildLegacyRecovery("pair", targetRoot, targetRoot.Length + 40, source, target, store.Load("pair"));

        // All 3 become conflicts:
        // A: absent from disk
        // B: metadata changed
        // C: destination already exists
        Assert.Equal(3, plan.Conflicts);
        Assert.Equal(0, plan.ActionCount);
        Assert.All(plan.Rows, r => Assert.Equal("conflict", r.Action));
    }

    [Fact]
    public void Legacy_hash_recovery_preserves_unrelated_files_in_old_directory()
    {
        var targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetRoot);

        var longDir = "Project Folder For Test";
        var legacyDir = "Project_#12345678";
        var originalFile = Path.Combine(longDir, "File Inside.pdf");
        var legacyFile = Path.Combine(legacyDir, "File_#aaaaaaaa.pdf");

        Directory.CreateDirectory(Path.Combine(targetRoot, legacyDir));
        File.WriteAllText(Path.Combine(targetRoot, legacyFile), "hello");

        // User placed an untracked file inside legacyDir
        var userFile = Path.Combine(targetRoot, legacyDir, "untracked-note.txt");
        File.WriteAllText(userFile, "important user note");

        var now = DateTimeOffset.UtcNow;
        var source = new Dictionary<string, ScanEntry>
        {
            [longDir] = new() { RelativePath = longDir, Kind = EntryKind.Directory },
            [originalFile] = new() { RelativePath = originalFile, Kind = EntryKind.File, Size = 5, MtimeUtc = now }
        };
        var target = new Dictionary<string, ScanEntry>
        {
            [legacyDir] = new() { RelativePath = legacyDir, Kind = EntryKind.Directory },
            [legacyFile] = new() { RelativePath = legacyFile, Kind = EntryKind.File, Size = 5, MtimeUtc = now },
            [Path.Combine(legacyDir, "untracked-note.txt")] = new() { RelativePath = Path.Combine(legacyDir, "untracked-note.txt"), Kind = EntryKind.File, Size = 19, MtimeUtc = now }
        };

        using var store = new StateStore(Path.Combine(_root, "state.db"));
        store.Upsert(new[]
        {
            new StateEntry
            {
                PairName = "pair", RelativePath = legacyDir, TargetPath = legacyDir, RemoteId = longDir,
                Kind = EntryKind.Directory, Status = EntryStatus.Active, FirstSeenUtc = now, LastSeenUtc = now
            },
            new StateEntry
            {
                PairName = "pair", RelativePath = legacyFile, TargetPath = legacyFile, RemoteId = originalFile,
                Kind = EntryKind.File, Status = EntryStatus.Active, FirstSeenUtc = now, LastSeenUtc = now,
                CopiedSize = 5, CopiedMtimeUtc = now
            }
        });

        var plan = TargetRepair.BuildLegacyRecovery("pair", targetRoot, targetRoot.Length + 40, source, target, store.Load("pair"));
        TargetRepair.ExecuteLegacyRecovery(plan, store);

        // The tracked file was moved to readable name
        var fileMove = plan.Rows.Single(r => r.OldPath.EndsWith(".pdf"));
        Assert.True(File.Exists(Path.Combine(targetRoot, fileMove.NewPath)));

        // The old directory was NOT deleted because it still contains untracked-note.txt!
        Assert.True(Directory.Exists(Path.Combine(targetRoot, legacyDir)));
        Assert.True(File.Exists(userFile));
    }

    [Fact]
    public void Legacy_recovery_followed_by_missing_file_audit_does_not_mark_moved_copies_as_missing()
    {
        var targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetRoot);

        var original = new string('С', 70) + ".pdf";
        var legacy = "ССС_#12345678.pdf";
        var oldFile = Path.Combine(targetRoot, legacy);
        File.WriteAllText(oldFile, "content");
        var now = DateTimeOffset.UtcNow;

        var source = new Dictionary<string, ScanEntry>
        {
            [original] = new() { RelativePath = original, Kind = EntryKind.File, Size = 7, MtimeUtc = now }
        };
        var target = new Dictionary<string, ScanEntry>
        {
            [legacy] = new() { RelativePath = legacy, Kind = EntryKind.File, Size = 7, MtimeUtc = now }
        };

        using var store = new StateStore(Path.Combine(_root, "state.db"));
        store.Upsert(new[]
        {
            new StateEntry
            {
                PairName = "pair", RelativePath = legacy, TargetPath = legacy, RemoteId = original,
                Kind = EntryKind.File, Status = EntryStatus.Active, FirstSeenUtc = now, LastSeenUtc = now,
                CopiedSize = 7, CopiedMtimeUtc = now
            }
        });

        // Execute legacy recovery
        var plan = TargetRepair.BuildLegacyRecovery("pair", targetRoot, targetRoot.Length + 40, source, target, store.Load("pair"));
        TargetRepair.ExecuteLegacyRecovery(plan, store);

        var movedRow = Assert.Single(plan.Rows);
        var freshTarget = new Dictionary<string, ScanEntry>
        {
            [movedRow.NewPath] = new() { RelativePath = movedRow.NewPath, Kind = EntryKind.File, Size = 7, MtimeUtc = now }
        };

        var pair = new FolderPair
        {
            Name = "pair",
            Source = Path.Combine(_root, "source"),
            Target = targetRoot,
            Mode = SyncMode.Mirror,
            ShortenLongPaths = true,
            MaxPathLength = targetRoot.Length + 40
        };

        // Missing file audit after migration
        var missingPlan = MissingFileRecovery.Build(pair, source, freshTarget, store.Load("pair"));
        Assert.Equal(0, missingPlan.Restorable);
        Assert.DoesNotContain(missingPlan.Items, i => i.Kind == MissingFileKind.ActiveMissing);
    }

    [Fact]
    public void TroubleshootingOptions_record_handles_legacy_hashes_flag()
    {
        var options = new TroubleshootingOptions(RepairPaths: false, AuditMissing: false, RestoreMissing: false, RecoverLegacyHashes: true);
        Assert.True(options.RecoverLegacyHashes);
        Assert.False(options.RepairPaths);
        Assert.False(options.AuditMissing);
        Assert.False(options.RestoreMissing);
    }
}
