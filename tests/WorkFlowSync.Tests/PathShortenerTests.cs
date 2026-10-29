using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Tests;

public sealed class PathShortenerTests
{
    private const string TargetRoot = @"C:\Users\teraxis\OneDrive - SuperLongOrganizationName\Documents\SyncedShare";

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
}
