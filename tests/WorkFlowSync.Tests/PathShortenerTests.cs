using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Tests;

public sealed class PathShortenerTests
{
    private const string TargetRoot = @"C:\Users\teraxis\OneDrive - SuperLongOrganizationName\Documents\SyncedShare";

    [Fact]
    public void Deep_sibling_files_keep_eight_character_hashes_and_share_shortened_ancestors()
    {
        var root = @"C:\" + new string('R', 62);
        var directories = new[] { new string('A', 17), new string('B', 70),
            new string('C', 19), new string('D', 19), new string('E', 56) };
        var parent = string.Join("\\", directories);
        var source = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 54; i++)
        {
            var path = parent + "\\" + new string('F', 41) + $"_{2017 + i / 12:D4}_{i % 12 + 1:D2}.xlsx";
            source[path] = new ScanEntry { RelativePath = path, Kind = EntryKind.File };
        }

        var mapped = PathShortener.MapEntries(source, root, true, 240, null, null, out _);
        Assert.Equal(54, mapped.Count);
        Assert.All(mapped.Keys, path =>
        {
            Assert.True(Path.Combine(root, path).Length <= 240, path);
            Assert.Matches(@"_#[0-9a-f]{8}\.xlsx$", path);
            Assert.DoesNotMatch(@"_#[0-9a-f]{1,7}(?:\.|\\|$)", path);
        });
        Assert.Single(mapped.Keys.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Contains("_#", Path.GetDirectoryName(mapped.Keys.First())!);
    }

    [Fact]
    public void Directory_ancestor_longer_than_limit_shortens_multiple_levels()
    {
        var root = @"C:\" + new string('R', 62);
        var relative = string.Join("\\", new string('A', 90), new string('B', 90),
            new string('C', 90), new string('D', 50), new string('E', 40) + ".txt");
        var source = new Dictionary<string, ScanEntry>
        {
            [relative] = new() { RelativePath = relative, Kind = EntryKind.File }
        };

        var mapped = PathShortener.MapEntries(source, root, true, 240, null, null, out _);
        var path = Assert.Single(mapped.Keys);
        Assert.True(Path.Combine(root, path).Length <= 240);
        Assert.True(path.Split('\\').Take(3).Count(p => p.Contains("_#")) >= 2);
        Assert.DoesNotMatch(@"_#[0-9a-f]{1,7}(?:\.|\\|$)", path);
    }

    [Fact]
    public void Five_thousand_similar_names_and_case_only_names_map_uniquely_and_repeatably()
    {
        var root = @"C:\" + new string('R', 62);
        var parent = new string('A', 80) + "\\" + new string('B', 70);
        var source = new Dictionary<string, ScanEntry>(StringComparer.Ordinal);
        for (var i = 0; i < 5000; i++)
        {
            var path = parent + "\\" + new string('C', 41) + $"_{i:D4}.xlsx";
            source[path] = new ScanEntry { RelativePath = path, Kind = EntryKind.File };
        }
        foreach (var name in new[] { "Case.xlsx", "case.xlsx" })
        {
            var path = parent + "\\" + name;
            source[path] = new ScanEntry { RelativePath = path, Kind = EntryKind.File };
        }

        var first = PathShortener.MapEntries(source, root, true, 240, null, null, out var firstOwners);
        var second = PathShortener.MapEntries(source, root, true, 240, null, null, out var secondOwners);
        var reversedSource = source.Reverse().ToDictionary(kvp => kvp.Key, kvp => kvp.Value,
            StringComparer.Ordinal);
        var reversed = PathShortener.MapEntries(reversedSource, root, true, 240, null, null,
            out var reversedOwners);
        Assert.Equal(source.Count, first.Count);
        Assert.Equal(first.Keys.Order(StringComparer.OrdinalIgnoreCase),
            second.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(first.Keys.Order(StringComparer.OrdinalIgnoreCase),
            reversed.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(firstOwners.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase),
            secondOwners.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(firstOwners.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase),
            reversedOwners.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Directory_hash_collision_uses_a_longer_hash()
    {
        var root = @"C:\" + new string('R', 62);
        var names = new[]
        {
            "ABCDEFG007144" + new string('X', 40),
            "ABCDEFG044148" + new string('X', 40),
        };
        var source = names.ToDictionary(n => n, n => new ScanEntry
        {
            RelativePath = n,
            Kind = EntryKind.Directory
        });

        var mapped = PathShortener.MapEntries(source, root, true, 115, null, null, out _);

        Assert.Equal(2, mapped.Count);
        Assert.All(mapped.Keys, path => Assert.True(Path.Combine(root, path).Length <= 115));
        Assert.Contains(mapped.Keys, path => System.Text.RegularExpressions.Regex.IsMatch(path,
            @"_#[0-9a-f]{12}$"));
    }

    [Fact]
    public void File_hash_collision_uses_a_longer_hash()
    {
        var root = @"C:\" + new string('R', 62);
        var names = new[]
        {
            "ABCDEFG054205" + new string('X', 40) + ".xlsx",
            "ABCDEFG074589" + new string('X', 40) + ".xlsx",
        };
        var source = names.ToDictionary(n => n, n => new ScanEntry
        {
            RelativePath = n,
            Kind = EntryKind.File
        });

        var mapped = PathShortener.MapEntries(source, root, true, 88, null, null, out _);

        Assert.Equal(2, mapped.Count);
        Assert.All(mapped.Keys, path => Assert.True(Path.Combine(root, path).Length <= 88));
        Assert.Contains(mapped.Keys, path => System.Text.RegularExpressions.Regex.IsMatch(path,
            @"_#[0-9a-f]{12}\.xlsx$"));
    }

    [Fact]
    public void New_file_does_not_claim_a_target_path_pinned_to_another_source()
    {
        var root = @"C:\" + new string('R', 62);
        var first = "ABCDEFG" + new string('X', 40) + ".xlsx";
        var second = "HIJKLMN" + new string('Y', 40) + ".xlsx";
        var firstOnly = new Dictionary<string, ScanEntry>
        {
            [first] = new() { RelativePath = first, Kind = EntryKind.File }
        };
        var claimed = PathShortener.MapEntries(firstOnly, root, true, 88, null, null,
            out _).Keys.Single();
        var source = new Dictionary<string, ScanEntry>(firstOnly)
        {
            [second] = new() { RelativePath = second, Kind = EntryKind.File }
        };
        var state = new Dictionary<string, StateEntry>
        {
            [claimed] = new()
            {
                PairName = "pair",
                RelativePath = claimed,
                TargetPath = claimed,
                RemoteId = second,
                Kind = EntryKind.File,
                FirstSeenUtc = DateTimeOffset.UtcNow
            }
        };

        var mapped = PathShortener.MapEntries(source, root, true, 88, state, null, out var owners);

        Assert.Equal(2, mapped.Count);
        Assert.Equal(second, owners[claimed]);
        Assert.Contains(mapped.Keys, path => !path.Equals(claimed, StringComparison.OrdinalIgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(path, @"_#[0-9a-f]{12}\.xlsx$"));
    }

    [Fact]
    public void ShortenFilePath_WhenWithinLimit_ReturnsUnchanged()
    {
        var relPath = @"Subfolder\RegularFile.docx";
        var maxLen = 240;

        var result = PathShortener.ShortenFilePath(TargetRoot, relPath, maxLen);

        Assert.Equal(relPath, result);
    }

    [Fact]
    public void ShortenFilePath_WhenExceedsLimit_ShortensFileNameOnly_PreservesExtensionAndAppendsHash()
    {
        var dir = @"Department\FinancialReports\QuarterlyAudits";
        var baseName = "Quarterly_Financial_Performance_Audit_Report_For_Q3_2026_Final_Approved_Signed_Document";
        var ext = ".docx";
        var relPath = Path.Combine(dir, baseName + ext);

        // TargetRoot.Length is 72. dir.Length is 41. Total prefix is ~114 chars.
        // If maxLen is 150, available for file name is ~35 chars. Original file name is 89 chars.
        var maxLen = 150;

        var shortened = PathShortener.ShortenFilePath(TargetRoot, relPath, maxLen);
        var fullTarget = Path.Combine(TargetRoot, shortened);

        Assert.True(fullTarget.Length <= maxLen, $"Expected length <= {maxLen}, got {fullTarget.Length} for '{fullTarget}'");
        Assert.EndsWith(ext, shortened, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(dir + "\\", shortened, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_#", shortened);
        Assert.StartsWith("Quarterly", Path.GetFileName(shortened), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShortenFilePath_TwoDifferentLongFiles_ProduceDifferentHashes_NoCollision()
    {
        var dir = @"Department\Reports";
        var fileA = Path.Combine(dir, "Very_Long_Document_Name_That_Exceeds_Limits_Version_A.pdf");
        var fileB = Path.Combine(dir, "Very_Long_Document_Name_That_Exceeds_Limits_Version_B.pdf");
        var maxLen = 120;

        var shortenedA = PathShortener.ShortenFilePath(TargetRoot, fileA, maxLen);
        var shortenedB = PathShortener.ShortenFilePath(TargetRoot, fileB, maxLen);

        Assert.NotEqual(shortenedA, shortenedB);
        Assert.True(Path.Combine(TargetRoot, shortenedA).Length <= maxLen);
        Assert.True(Path.Combine(TargetRoot, shortenedB).Length <= maxLen);
        Assert.EndsWith(".pdf", shortenedA);
        Assert.EndsWith(".pdf", shortenedB);
    }

    [Fact]
    public void ShortenFilePath_WhenDirectoryAloneIsVeryLong_ShortensDirectorySegments()
    {
        var deepDir = @"VeryLongFirstDirectorySegmentName\AnotherExtremelyLongSecondDirectorySegmentName\ThirdDeeplyNestedDirectorySegment";
        var file = Path.Combine(deepDir, "Short.txt");
        // Deep dir itself is 114 chars + TargetRoot (72) = 186 chars.
        // If maxLen is 140, even shortening file name to 1 char won't fit without shortening directories!
        var maxLen = 140;

        var shortened = PathShortener.ShortenFilePath(TargetRoot, file, maxLen);
        var fullTarget = Path.Combine(TargetRoot, shortened);

        Assert.True(fullTarget.Length <= maxLen, $"Expected length <= {maxLen}, got {fullTarget.Length} for '{fullTarget}'");
        Assert.EndsWith(".txt", shortened, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_#", shortened);
    }

    [Fact]
    public void MapEntries_WhenDisabled_ReturnsOriginalEntries()
    {
        var entries = new Dictionary<string, ScanEntry>
        {
            ["A\\VeryLongFileNameThatExceedsTheConfiguredMaximumAllowedPathLengthByManyCharacters.txt"] = new()
            {
                RelativePath = "A\\VeryLongFileNameThatExceedsTheConfiguredMaximumAllowedPathLengthByManyCharacters.txt",
                Kind = EntryKind.File,
                Size = 100,
                MtimeUtc = DateTimeOffset.UtcNow,
            }
        };

        var mapped = PathShortener.MapEntries(entries, TargetRoot, enabled: false, maxPathLength: 100, state: null, existingTargetToSourceMap: null, out var map);

        Assert.Same(entries, mapped);
        Assert.Empty(map);
    }

    [Fact]
    public void MapEntries_WhenEnabled_ProjectsLongPathsAndBuildsTargetToSourceMap()
    {
        var longRel = "A\\Super_Extremely_Long_File_Name_For_Important_Corporate_Documentation_2026_Version_Final.pdf";
        var shortRel = "A\\Short.pdf";
        var entries = new Dictionary<string, ScanEntry>
        {
            ["A"] = new() { RelativePath = "A", Kind = EntryKind.Directory, MtimeUtc = DateTimeOffset.UtcNow },
            [shortRel] = new() { RelativePath = shortRel, Kind = EntryKind.File, Size = 50, MtimeUtc = DateTimeOffset.UtcNow },
            [longRel] = new() { RelativePath = longRel, Kind = EntryKind.File, Size = 500, MtimeUtc = DateTimeOffset.UtcNow }
        };

        var maxLen = 120;
        var mapped = PathShortener.MapEntries(entries, TargetRoot, enabled: true, maxPathLength: maxLen, state: null, existingTargetToSourceMap: null, out var targetToSourceMap);

        // Short entry stays unchanged
        Assert.True(mapped.ContainsKey(shortRel));
        Assert.False(targetToSourceMap.ContainsKey(shortRel));

        // Long entry is projected to a shortened key
        Assert.False(mapped.ContainsKey(longRel));
        var projectedEntry = mapped.Values.FirstOrDefault(v => v.Kind == EntryKind.File && v.RelativePath != shortRel);
        Assert.NotNull(projectedEntry);
        Assert.True(Path.Combine(TargetRoot, projectedEntry.RelativePath).Length <= maxLen);
        Assert.True(targetToSourceMap.TryGetValue(projectedEntry.RelativePath, out var originalSource));
        Assert.Equal(longRel, originalSource);
    }

    [Fact]
    public void MapEntries_PreservesPreviouslyShortenedPathFromStateRemoteId()
    {
        var originalSource = "Deep\\Extremely_Long_Source_Path_That_Was_Shortened_Previously.docx";
        var previouslyShortened = "Deep\\Extrem_#12345678.docx";

        var state = new Dictionary<string, StateEntry>
        {
            [previouslyShortened] = new()
            {
                PairName = "test",
                RelativePath = previouslyShortened,
                Kind = EntryKind.File,
                FirstSeenUtc = DateTimeOffset.UtcNow,
                RemoteId = originalSource
            }
        };

        var source = new Dictionary<string, ScanEntry>
        {
            [originalSource] = new()
            {
                RelativePath = originalSource,
                Kind = EntryKind.File,
                Size = 1024,
                MtimeUtc = DateTimeOffset.UtcNow
            }
        };

        var mapped = PathShortener.MapEntries(source, TargetRoot, enabled: true, maxPathLength: 120, state: state, existingTargetToSourceMap: null, out var map);

        Assert.True(mapped.ContainsKey(previouslyShortened));
        Assert.Equal(previouslyShortened, mapped[previouslyShortened].RelativePath);
        Assert.Equal(originalSource, map[previouslyShortened]);
    }

    [Fact]
    public void SyncPlanner_WithShortenLongPaths_CopiesShortenedFile_AndSecondPassIsUnchanged()
    {
        var longRelPath = @"Reports\Detailed_Annual_Company_Audit_And_Compliance_Documentation_2026_Full_Report.docx";
        var maxLen = 130;
        var now = DateTimeOffset.UtcNow;

        var source = new Dictionary<string, ScanEntry>
        {
            ["Reports"] = new() { RelativePath = "Reports", Kind = EntryKind.Directory, MtimeUtc = now },
            [longRelPath] = new() { RelativePath = longRelPath, Kind = EntryKind.File, Size = 4096, MtimeUtc = now }
        };
        var target = new Dictionary<string, ScanEntry>();
        var state = new Dictionary<string, StateEntry>();

        var planner = new SyncPlanner("pair1", now, backfillFirstSeen: false,
            targetRoot: TargetRoot, shortenLongPaths: true, maxPathLength: maxLen);

        // PASS 1: First copy
        var plan1 = planner.Plan(source, target, state);

        Assert.Equal(2, plan1.Stats.New); // 1 directory + 1 file
        var copyAction = plan1.Actions.FirstOrDefault(a => a.Kind == SyncActionKind.CopyFile);
        Assert.NotNull(copyAction);
        Assert.NotEqual(longRelPath, copyAction.RelativePath); // Shortened!
        Assert.Equal(longRelPath, copyAction.SourceRelativePath); // Preserved original source path!
        Assert.Equal(longRelPath, copyAction.Proposed.RemoteId); // Stored in proposed state entry!

        var shortenedTargetRel = copyAction.RelativePath;

        // Simulate execution: file lands on target with shortened name, state entry recorded
        target["Reports"] = new() { RelativePath = "Reports", Kind = EntryKind.Directory, MtimeUtc = now };
        target[shortenedTargetRel] = new() { RelativePath = shortenedTargetRel, Kind = EntryKind.File, Size = 4096, MtimeUtc = now };
        state["Reports"] = new() { PairName = "pair1", RelativePath = "Reports", Kind = EntryKind.Directory, FirstSeenUtc = now };
        state[shortenedTargetRel] = copyAction.Proposed with
        {
            CopiedSize = 4096,
            CopiedMtimeUtc = now,
            CopiedAtUtc = now
        };

        // PASS 2: Next pass with same source, target, and state
        var plan2 = planner.Plan(source, target, state);

        // Must be UNCHANGED! No duplicate copy, no re-download, no error!
        Assert.Equal(0, plan2.Stats.New);
        Assert.Equal(0, plan2.Stats.Updated);
        Assert.Equal(2, plan2.Stats.Unchanged); // 1 directory ("Reports") + 1 file
        Assert.Empty(plan2.Actions.Where(a => a.Kind is SyncActionKind.CopyFile or SyncActionKind.UpdateFile));
    }

    [Fact]
    public void SyncPlanner_WithShortenLongPaths_WhenSourceModified_UpdatesShortenedFile()
    {
        var longRelPath = @"Reports\Very_Long_Document_Name_Exceeding_Limits_2026.docx";
        var maxLen = 130;
        var now = DateTimeOffset.UtcNow;
        var updatedTime = now.AddHours(2);

        var planner = new SyncPlanner("pair1", updatedTime, backfillFirstSeen: false,
            targetRoot: TargetRoot, shortenLongPaths: true, maxPathLength: maxLen);

        var shortenedTargetRel = PathShortener.ShortenFilePath(TargetRoot, longRelPath, maxLen);

        // Target and state hold old version
        var target = new Dictionary<string, ScanEntry>
        {
            [shortenedTargetRel] = new() { RelativePath = shortenedTargetRel, Kind = EntryKind.File, Size = 4096, MtimeUtc = now }
        };
        var state = new Dictionary<string, StateEntry>
        {
            [shortenedTargetRel] = new()
            {
                PairName = "pair1",
                RelativePath = shortenedTargetRel,
                Kind = EntryKind.File,
                FirstSeenUtc = now,
                SourceSize = 4096,
                SourceMtimeUtc = now,
                CopiedSize = 4096,
                CopiedMtimeUtc = now,
                RemoteId = longRelPath
            }
        };

        // Source has newer modified time and larger size
        var source = new Dictionary<string, ScanEntry>
        {
            [longRelPath] = new() { RelativePath = longRelPath, Kind = EntryKind.File, Size = 8192, MtimeUtc = updatedTime }
        };

        var plan = planner.Plan(source, target, state);

        Assert.Equal(1, plan.Stats.Updated);
        var updateAction = plan.Actions.FirstOrDefault(a => a.Kind == SyncActionKind.UpdateFile);
        Assert.NotNull(updateAction);
        Assert.Equal(shortenedTargetRel, updateAction.RelativePath);
        Assert.Equal(longRelPath, updateAction.SourceRelativePath);
        Assert.Equal(longRelPath, updateAction.Proposed.RemoteId);
        Assert.Equal(8192, updateAction.Proposed.SourceSize);
    }

    [Fact]
    public void ConfigValidation_MaxPathLength_ValidatesRange()
    {
        var config = new SyncConfig
        {
            Pairs =
            {
                new FolderPair
                {
                    Name = "pair1",
                    Source = @"C:\src",
                    Target = @"C:\dst",
                    ShortenLongPaths = true,
                    MaxPathLength = 50 // Too small (< 80)
                }
            }
        };

        var problems = config.Validate();
        Assert.Contains(problems, p => p.Contains("maxPathLength must be between 80 and 1000"));

        config.Pairs[0].MaxPathLength = 240;
        Assert.Empty(config.Validate());
    }

    [Fact]
    public void ShortenDirectoryPath_WhenCyrillicSegmentExceeds255Utf8Bytes_ShortensSegmentUtf8Bytes()
    {
        var root = @"G:\Мій диск\Дисциплінарні провадження";
        // 137 Cyrillic characters = 256 UTF-8 bytes (> 255 byte Google DriveFS limit), even though total chars is only 186 (< 240)
        var longSegment = "Головне управління з протидії системним загрозам управлінню державою Департаменту захисту національної державності Служби безпеки України";
        var relDir = @"Березін А.Г\" + longSegment;

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(longSegment) > 255);
        Assert.True(Path.Combine(root, relDir).Length < 240);

        var shortened = PathShortener.ShortenDirectoryPath(root, relDir, 240);
        var shortenedSegments = shortened.Split('\\');

        Assert.NotEqual(relDir, shortened);
        Assert.All(shortenedSegments, seg =>
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(seg) <= PathShortener.MaxSegmentUtf8Bytes));
    }

    [Fact]
    public void MapEntries_WhenDirectorySegmentHasLeadingSpace_TrimsLeadingSpaceAndMapsOriginalSource()
    {
        var root = @"G:\Мій диск\Дисциплінарні провадження";
        var dirWithLeadingSpace = @"Мороз Н.В\№914-1165-24\ Визнання права власності ЗАГС 06-11";
        var fileUnderDir = dirWithLeadingSpace + @"\doc.pdf";
        var now = DateTimeOffset.UtcNow;

        var source = new Dictionary<string, ScanEntry>
        {
            [dirWithLeadingSpace] = new() { RelativePath = dirWithLeadingSpace, Kind = EntryKind.Directory, MtimeUtc = now },
            [fileUnderDir] = new() { RelativePath = fileUnderDir, Kind = EntryKind.File, Size = 100, MtimeUtc = now },
        };

        var mapped = PathShortener.MapEntries(source, root, enabled: true, maxPathLength: 240, state: null, existingTargetToSourceMap: null, out var map);

        var expectedDir = @"Мороз Н.В\№914-1165-24\Визнання права власності ЗАГС 06-11";
        var expectedFile = expectedDir + @"\doc.pdf";

        Assert.True(mapped.ContainsKey(expectedDir));
        Assert.True(mapped.ContainsKey(expectedFile));
        Assert.Equal(dirWithLeadingSpace, map[expectedDir]);
        Assert.Equal(fileUnderDir, map[expectedFile]);
    }

    [Fact]
    public void Directory_mapping_is_shared_by_short_and_long_sibling_files()
    {
        var dir = "Archive_" + new string('A', 90);
        var names = new[] { "short.txt", new string('B', 60) + ".txt", new string('C', 150) + ".txt" };
        var source = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [dir] = new() { RelativePath = dir, Kind = EntryKind.Directory },
        };
        foreach (var name in names)
        {
            var path = Path.Combine(dir, name);
            source[path] = new ScanEntry { RelativePath = path, Kind = EntryKind.File };
        }

        var mapped = PathShortener.MapEntries(source, TargetRoot, true, 200, null, null, out _);
        var mappedDir = mapped.Values.Single(e => e.Kind == EntryKind.Directory).RelativePath;
        Assert.All(mapped.Values.Where(e => e.Kind == EntryKind.File), e =>
        {
            Assert.Equal(mappedDir, Path.GetDirectoryName(e.RelativePath));
            Assert.True(Path.Combine(TargetRoot, e.RelativePath).Length <= 200);
        });
    }

    [Fact]
    public void Same_directory_name_in_different_branches_has_different_hash()
    {
        var longName = new string('D', 90);
        var dirs = new[] { Path.Combine("Left", longName), Path.Combine("Right", longName) };
        var source = dirs.ToDictionary(d => d, d => new ScanEntry { RelativePath = d, Kind = EntryKind.Directory });
        var mapped = PathShortener.MapEntries(source, TargetRoot, true, 200, null, null, out _);
        Assert.Equal(2, mapped.Count);
        Assert.NotEqual(Path.GetFileName(mapped.Values.First().RelativePath),
            Path.GetFileName(mapped.Values.Last().RelativePath));
    }

    [Fact]
    public void Recorded_original_target_path_survives_tighter_limit_and_disabled_shortening()
    {
        var original = "A\\" + new string('E', 150) + ".txt";
        var state = new Dictionary<string, StateEntry>
        {
            [original] = new()
            {
                PairName = "test",
                RelativePath = original,
                TargetPath = original,
                Kind = EntryKind.File,
                FirstSeenUtc = DateTimeOffset.UtcNow,
            }
        };
        var source = new Dictionary<string, ScanEntry>
        {
            [original] = new() { RelativePath = original, Kind = EntryKind.File }
        };
        var tightened = PathShortener.MapEntries(source, TargetRoot, true, 120, state, null, out _);
        var disabled = PathShortener.MapEntries(source, TargetRoot, false, 120, state, null, out _);
        Assert.True(tightened.ContainsKey(original));
        Assert.True(disabled.ContainsKey(original));
    }

    [Fact]
    public void Effective_limit_respects_windows_and_onedrive_separately()
    {
        var root = @"C:\Users\User\OneDrive";
        Assert.Equal(259, PathLimits.Compute(root, 300, longPathsEnabled: false, root));
        Assert.Equal(300, PathLimits.Compute(root, 300, longPathsEnabled: true, root));
        Assert.Equal(root.Length + 1 + 400, PathLimits.Compute(root, 1000, longPathsEnabled: true, root));
    }

    [Fact]
    public void Planner_does_not_recopy_recorded_original_after_limit_changes()
    {
        var now = DateTimeOffset.UtcNow;
        var original = "Archive\\" + new string('F', 130) + ".txt";
        var source = new Dictionary<string, ScanEntry>
        {
            [original] = new() { RelativePath = original, Kind = EntryKind.File, Size = 8, MtimeUtc = now }
        };
        var target = new Dictionary<string, ScanEntry>
        {
            [original] = new() { RelativePath = original, Kind = EntryKind.File, Size = 8, MtimeUtc = now }
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
                LastSeenUtc = now,
                SourceSize = 8,
                SourceMtimeUtc = now,
                CopiedSize = 8,
                CopiedMtimeUtc = now,
            }
        };
        var planner = new SyncPlanner("pair", now, false, targetRoot: TargetRoot,
            shortenLongPaths: true, maxPathLength: 120);
        var plan = planner.Plan(source, target, state);
        Assert.Equal(0, plan.Stats.New);
        Assert.Equal(1, plan.Stats.Unchanged);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void New_shortening_is_reported_in_execution_log_and_summary()
    {
        var sourcePath = new string('G', 100) + ".txt";
        var now = DateTimeOffset.UtcNow;
        var source = new Dictionary<string, ScanEntry>
        {
            [sourcePath] = new() { RelativePath = sourcePath, Kind = EntryKind.File, Size = 3, MtimeUtc = now }
        };
        var plan = new SyncPlanner("pair", now, false, targetRoot: TargetRoot,
            shortenLongPaths: true, maxPathLength: 120)
            .Plan(source, new Dictionary<string, ScanEntry>(), new Dictionary<string, StateEntry>());
        var log = new MemorySyncLog();
        var result = new SyncExecutor(Path.GetTempPath(), Path.GetTempPath(), log, dryRun: true)
            .Execute(plan, "[pair]");
        Assert.Equal(1, result.Shortened);
        Assert.Contains(log.Lines, line => line.Message.Contains("shorten") && line.Message.Contains(sourcePath));
    }
}
