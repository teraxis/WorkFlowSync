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
            { "name": "docs", "source": "E:\\docs", "target": "D:\\Sync\\docs", "links": "follow", "retention": "365.00:00:00" }
          ],
          "interval": "00:30:00"
        }
        """;

        var cfg = SyncConfig.Parse(json);

        Assert.Single(cfg.Pairs);
        Assert.Equal(LinkMode.Follow, cfg.Pairs[0].Links);
        Assert.Equal(TimeSpan.FromDays(365), cfg.Pairs[0].MaxAge);
        Assert.Equal(TimeSpan.FromMinutes(30), cfg.Interval);
        Assert.Empty(cfg.Validate());
    }

    /// <summary>
    /// A config written before 2026-09-17 carried one "retention" window. Its checkbox promised an intake
    /// filter, so that is what it becomes; auto-clean is a new, separate choice and starts off. The legacy
    /// name is not written back — one save and it is gone.
    /// </summary>
    [Fact]
    public void Legacy_retention_becomes_the_intake_window_and_is_not_written_back()
    {
        var cfg = SyncConfig.Parse("""
        { "pairs": [ { "name": "vrp", "source": "E:\\vrp", "target": "D:\\dst", "retention": "365.00:00:00" } ] }
        """);

        Assert.Equal(TimeSpan.FromDays(365), cfg.Pairs[0].MaxAge);
        Assert.Null(cfg.Pairs[0].AutoClean);

        var json = cfg.ToJson();
        Assert.DoesNotContain("Retention", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"MaxAge\"", json);
        Assert.Equal(TimeSpan.FromDays(365), SyncConfig.Parse(json).Pairs[0].MaxAge);
    }

    /// <summary>An explicit new-style window wins; the legacy name never overwrites it, whatever the key order.</summary>
    [Fact]
    public void An_explicit_max_age_is_not_overwritten_by_a_legacy_retention_key()
    {
        var before = SyncConfig.Parse("""
        { "pairs": [ { "name": "p", "source": "a", "target": "b", "maxAge": "10.00:00:00", "retention": "365.00:00:00" } ] }
        """);
        var after = SyncConfig.Parse("""
        { "pairs": [ { "name": "p", "source": "a", "target": "b", "retention": "365.00:00:00", "maxAge": "10.00:00:00" } ] }
        """);

        Assert.Equal(TimeSpan.FromDays(10), before.Pairs[0].MaxAge);
        Assert.Equal(TimeSpan.FromDays(10), after.Pairs[0].MaxAge);
    }

    [Fact]
    public void Auto_clean_is_rejected_on_a_two_way_pair()
    {
        var cfg = new SyncConfig
        {
            Pairs = { new FolderPair { Name = "p", Source = @"C:\a", Target = @"C:\b", Mode = SyncMode.TwoWay, AutoClean = TimeSpan.FromDays(30) } },
        };

        Assert.Contains(cfg.Validate(), p => p.Contains("autoClean", StringComparison.Ordinal));

        cfg.Pairs[0].Mode = SyncMode.Mirror;
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

    [Fact]
    public void Approval_and_versioning_config_validation()
    {
        var mirrorApproval = new SyncConfig
        {
            Pairs = { new FolderPair { Name = "p", Source = @"D:\src", Target = @"D:\dst", Mode = SyncMode.Mirror, RequireApproval = true } }
        };
        Assert.Contains(mirrorApproval.Validate(), p => p.Contains("requireApproval"));

        var invalidDays = new SyncConfig
        {
            Pairs = { new FolderPair { Name = "p", Source = @"D:\src", Target = @"D:\dst", Mode = SyncMode.TwoWay, RequireApproval = true, VersionsKeepDays = 0 } }
        };
        Assert.Contains(invalidDays.Validate(), p => p.Contains("versionsKeepDays"));

        var validTwoWay = new SyncConfig
        {
            Pairs = { new FolderPair { Name = "p", Source = @"D:\src", Target = @"D:\dst", Mode = SyncMode.TwoWay, RequireApproval = true, Versioning = true, VersionsKeepDays = 30, VersionsMaxGb = 5 } }
        };
        Assert.Empty(validTwoWay.Validate());
    }
}
