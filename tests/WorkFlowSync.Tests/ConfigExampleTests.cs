using WorkFlowSync.Core.Config;

namespace WorkFlowSync.Tests;

public class ConfigExampleTests
{
    [Fact]
    public void Config_example_is_valid_and_contains_no_user_specific_data()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? configPath = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config.example.json");
            if (File.Exists(candidate))
            {
                configPath = candidate;
                break;
            }
            dir = dir.Parent;
        }

        Assert.NotNull(configPath);
        Assert.True(File.Exists(configPath));

        var raw = File.ReadAllText(configPath);

        // Disallow sensitive/user personal paths and identifiers
        string[] forbidden = ["S:\\", "OneDrive", "ВРП", "tmp\\test", "teraxis"];
        foreach (var item in forbidden)
        {
            Assert.DoesNotContain(item, raw, StringComparison.OrdinalIgnoreCase);
        }

        // Must parse and pass full validation
        var config = SyncConfig.Load(configPath);
        var problems = config.Validate();
        Assert.Empty(problems);

        // Ensure pairs have generic placeholder paths
        Assert.NotEmpty(config.Pairs);
        foreach (var pair in config.Pairs)
        {
            Assert.False(string.IsNullOrWhiteSpace(pair.Source));
            Assert.False(string.IsNullOrWhiteSpace(pair.Target));
        }
    }
}
