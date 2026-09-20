using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Tests;

public class ExcludeMatcherTests
{
    [Theory]
    [InlineData(@"\Тимчасове", @"Тимчасове", true)]
    [InlineData(@"\Тимчасове", @"Тимчасове\a.docx", true)]          // subtree
    [InlineData(@"\Тимчасове", @"Інше\Тимчасове\a.docx", false)]    // anchored: only at root
    [InlineData("*.tmp", @"a.tmp", true)]
    [InlineData("*.tmp", @"x\y\b.TMP", true)]                        // case-insensitive, anywhere
    [InlineData("*.tmp", @"x\y\b.tmpx", false)]
    [InlineData(@"~$*", @"docs\~$report.docx", true)]
    [InlineData(@"~$*", @"docs\a~$report.docx", false)]               // must start at a segment boundary
    [InlineData(@"\Проєкти\*Робочий.docx", @"Проєкти\2025_рік\10_Робочий.docx", true)]
    [InlineData(@"\Проєкти\*Робочий.docx", @"Проєкти\2025_рік\10_Робочий.docx\x", true)]
    [InlineData(@"\Проєкти\*Робочий.docx", @"Проєкти\2025_рік\10.docx", false)]
    [InlineData("obj", @"src\obj\x.o", true)]
    [InlineData("obj", @"src\object\x.o", false)]
    public void Matches_like_freefilesync(string pattern, string path, bool expected)
    {
        var m = new ExcludeMatcher(new[] { pattern });
        Assert.Equal(expected, m.IsExcluded(path));
    }

    [Fact]
    public void Empty_matcher_excludes_nothing()
    {
        Assert.False(ExcludeMatcher.Empty.IsExcluded(@"anything\at\all"));
        Assert.Equal(0, new ExcludeMatcher(new[] { "", "  " }).Count);
    }
}
