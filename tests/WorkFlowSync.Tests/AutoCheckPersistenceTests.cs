using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.Tests;

/// <summary>«Перевіряти автоматично» must survive a restart (docs/product/features/gui.md, «Стан»).</summary>
[Trait("Category", "E2E")]
public sealed class AutoCheckPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-auto-" + Guid.NewGuid().ToString("N"));
    private readonly string _configPath;

    public AutoCheckPersistenceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        _configPath = Path.Combine(_root, "config.json");
        ConfigFile.Save(new SyncConfig
        {
            Pairs = { new FolderPair { Name = "t", Source = Path.Combine(_root, "src"), Target = Path.Combine(_root, "dst") } },
            Interval = TimeSpan.FromMinutes(30),
        }, _configPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Switch_is_written_to_the_config_and_restored_on_the_next_start()
    {
        var vm = new MainViewModel(_configPath);
        Assert.False(vm.Run.AutoCheck);
        Assert.False(vm.Background.IsActive);

        vm.Run.AutoCheck = true;
        Assert.True(vm.Background.IsActive);
        Assert.True(SyncConfig.Load(_configPath).AutoCheck);          // persisted immediately (config was clean)
        vm.Background.Stop();

        var restarted = new MainViewModel(_configPath);
        Assert.True(restarted.Run.AutoCheck);
        Assert.True(restarted.Background.IsActive);                    // resumed by itself
        restarted.Background.Stop();

        restarted.Run.AutoCheck = false;
        Assert.False(SyncConfig.Load(_configPath).AutoCheck);
        Assert.False(restarted.Background.IsActive);
    }

    [Fact]
    public void Switch_refuses_to_start_while_there_are_unsaved_changes()
    {
        var vm = new MainViewModel(_configPath);
        vm.IntervalMinutes = 2;                    // unsaved edit
        Assert.True(vm.IsDirty);

        vm.Run.AutoCheck = true;

        Assert.False(vm.Run.AutoCheck);
        Assert.False(vm.Background.IsActive);
        Assert.Contains("Зберегти", vm.Run.BackgroundStatus);
        Assert.False(SyncConfig.Load(_configPath).AutoCheck);

        vm.SaveCommand.Execute(null);
        vm.Run.AutoCheck = true;
        Assert.True(vm.Background.IsActive);
        Assert.Equal(2, SyncConfig.Load(_configPath).Interval.TotalMinutes);
        Assert.True(SyncConfig.Load(_configPath).AutoCheck);
        vm.Background.Stop();
    }

    [Fact]
    public void Old_configs_without_the_field_start_with_checking_off()
    {
        var cfg = SyncConfig.Parse("""{ "pairs": [ { "name": "x", "source": "s", "target": "t" } ] }""");
        Assert.False(cfg.AutoCheck);
    }
}
