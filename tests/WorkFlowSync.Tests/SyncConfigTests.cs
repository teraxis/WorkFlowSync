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
}
