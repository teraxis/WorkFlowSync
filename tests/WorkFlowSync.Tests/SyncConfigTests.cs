using WorkFlowSync.Core.Config;

namespace WorkFlowSync.Tests;

public class SyncConfigTests
{
    [Fact]
    public void Parses_example_config_with_enum_and_timespan()
    {
        const string json = """
        {
          // comments are allowed
          "pairs": [
            { "name": "vrp", "source": "E:\\vrp", "target": "D:\\OneDrive\\Робоча папка", "links": "follow", "retention": "365.00:00:00" }
          ],
          "interval": "00:30:00"
        }
        """;

        var cfg = SyncConfig.Parse(json);

        Assert.Single(cfg.Pairs);
        Assert.Equal(LinkMode.Follow, cfg.Pairs[0].Links);
        Assert.Equal(TimeSpan.FromDays(365), cfg.Pairs[0].Retention);
        Assert.Equal(TimeSpan.FromMinutes(30), cfg.Interval);
        Assert.Empty(cfg.Validate());
    }

    [Fact]
    public void Validate_reports_missing_pairs_and_duplicates()
    {
        var empty = new SyncConfig();
        Assert.Contains(empty.Validate(), p => p.Contains("No folder pairs"));

        var dup = new SyncConfig
        {
            Pairs =
            {
                new FolderPair { Name = "a", Source = "x", Target = "y" },
                new FolderPair { Name = "A", Source = "x", Target = "y" },
            },
        };
        Assert.Contains(dup.Validate(), p => p.Contains("Duplicate"));
    }
    [Fact]
    public void Validate_rejects_a_pair_inside_itself()
    {
        static SyncConfig Pair(string src, string dst) => new() { Pairs = { new FolderPair { Name = "a", Source = src, Target = dst } } };

        Assert.Contains(Pair(@"D:\Work", @"D:\Work").Validate(), p => p.Contains("same folder"));
        Assert.Contains(Pair(@"D:\Work", @"D:\Work\Mirror").Validate(), p => p.Contains("target is inside"));
        Assert.Contains(Pair(@"D:\Work\Sub", @"D:\Work").Validate(), p => p.Contains("source is inside"));

        // Sibling folders, and a shared prefix that is not a parent, are fine.
        Assert.Empty(Pair(@"D:\Work", @"D:\Backup").Validate());
        Assert.Empty(Pair(@"D:\Work", @"D:\Work-2").Validate());
    }

    [Fact]
    public void Mode_defaults_to_mirror_and_round_trips()
    {
        Assert.Equal(SyncMode.Mirror, new FolderPair().Mode);
        var cfg = new SyncConfig { Pairs = { new FolderPair { Name = "a", Source = @"D:\x", Target = @"D:\y", Mode = SyncMode.TwoWay } } };
        Assert.Equal(SyncMode.TwoWay, SyncConfig.Parse(cfg.ToJson()).Pairs[0].Mode);
    }
}
