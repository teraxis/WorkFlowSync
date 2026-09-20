using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Tests;

public class MoveDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Detects_file_rename_in_same_directory()
    {
        var missing = new[]
        {
            new ScanEntry { RelativePath = @"docs\old_name.docx", Kind = EntryKind.File, Size = 1024, MtimeUtc = T0 },
        };
        var appeared = new[]
        {
            new ScanEntry { RelativePath = @"docs\new_name.docx", Kind = EntryKind.File, Size = 1024, MtimeUtc = T0 },
        };

        var (matches, unmatchedMissing, unmatchedAppeared) = MoveDetector.Detect(missing, appeared);

        Assert.Single(matches);
        Assert.Empty(unmatchedMissing);
        Assert.Empty(unmatchedAppeared);

        var match = matches[0];
        Assert.Equal(@"docs\old_name.docx", match.OldItem.RelativePath);
        Assert.Equal(@"docs\new_name.docx", match.NewItem.RelativePath);
        Assert.Equal(PendingChange.Renamed, match.ChangeType);
        Assert.False(match.IsCaseOnlyRename);
    }

    [Fact]
    public void Detects_file_move_between_directories()
    {
        var missing = new[]
        {
            new ScanEntry { RelativePath = @"folderA\report.pdf", Kind = EntryKind.File, Size = 2048, MtimeUtc = T0 },
        };
        var appeared = new[]
        {
            new ScanEntry { RelativePath = @"folderB\report.pdf", Kind = EntryKind.File, Size = 2048, MtimeUtc = T0 },
        };

        var (matches, unmatchedMissing, unmatchedAppeared) = MoveDetector.Detect(missing, appeared);

        Assert.Single(matches);
        Assert.Equal(PendingChange.Moved, matches[0].ChangeType);
        Assert.False(matches[0].IsCaseOnlyRename);
    }

    [Fact]
    public void Detects_case_only_rename()
    {
        var missing = new[]
        {
            new ScanEntry { RelativePath = @"docs\Звіт.docx", Kind = EntryKind.File, Size = 500, MtimeUtc = T0 },
        };
        var appeared = new[]
        {
            new ScanEntry { RelativePath = @"docs\ЗВІТ.docx", Kind = EntryKind.File, Size = 500, MtimeUtc = T0 },
        };

        var (matches, unmatchedMissing, unmatchedAppeared) = MoveDetector.Detect(missing, appeared);

        Assert.Single(matches);
        Assert.True(matches[0].IsCaseOnlyRename);
        Assert.Equal(PendingChange.Renamed, matches[0].ChangeType);
    }

    [Fact]
    public void Zero_byte_files_are_never_glued()
    {
        var missing = new[]
        {
            new ScanEntry { RelativePath = @"empty1.txt", Kind = EntryKind.File, Size = 0, MtimeUtc = T0 },
        };
        var appeared = new[]
        {
            new ScanEntry { RelativePath = @"empty2.txt", Kind = EntryKind.File, Size = 0, MtimeUtc = T0 },
        };

        var (matches, unmatchedMissing, unmatchedAppeared) = MoveDetector.Detect(missing, appeared);

        Assert.Empty(matches);
        Assert.Single(unmatchedMissing);
        Assert.Single(unmatchedAppeared);
    }

    [Fact]
    public void Ambiguous_multiple_candidates_are_not_glued()
    {
        var missing = new[]
        {
            new ScanEntry { RelativePath = @"original.txt", Kind = EntryKind.File, Size = 100, MtimeUtc = T0 },
        };
        var appeared = new[]
        {
            new ScanEntry { RelativePath = @"copy1.txt", Kind = EntryKind.File, Size = 100, MtimeUtc = T0 },
            new ScanEntry { RelativePath = @"copy2.txt", Kind = EntryKind.File, Size = 100, MtimeUtc = T0 },
        };

        var (matches, unmatchedMissing, unmatchedAppeared) = MoveDetector.Detect(missing, appeared);

        Assert.Empty(matches);
        Assert.Single(unmatchedMissing);
        Assert.Equal(2, unmatchedAppeared.Count);
    }
}
