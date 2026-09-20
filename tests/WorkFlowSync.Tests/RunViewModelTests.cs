using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.Tests;

[Collection("I18n Tests")]
[Trait("Category", "E2E")]
public sealed class RunViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-vm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Dry_run_then_real_run_from_the_gui_view_model()
    {
        var src = Path.Combine(_root, "src");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "doc.txt"), "hello");
        var configPath = Path.Combine(_root, "config.json");
        // Paused on disk so the automatic checker does not race this test; enabled in memory so «Синхронізувати» includes it.
        ConfigFile.Save(new SyncConfig { Pairs = { new FolderPair { Name = "t", Source = src, Target = dst, Enabled = false } } }, configPath);

        var main = new MainViewModel(configPath);
        main.Background.Pause();
        main.Pairs[0].Enabled = true;
        main.Background.Stop();
        var run = main.Run;
        Assert.Contains("Проходів ще не було", run.Summary);

        await run.DryRunCommand.ExecuteAsync(null);
        Assert.False(run.IsRunning);
        Assert.Contains("План готовий: 1", run.LastResult);
        Assert.False(Directory.Exists(dst));
        Assert.Contains("[dry-run] copy", run.LogText);

        await run.RunNowCommand.ExecuteAsync(null);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dst, "doc.txt")));
        Assert.Contains("скопійовано/оновлено 1", run.LastResult);
        Assert.Contains("дзеркалюється: 1", run.Summary);
        Assert.Contains("Останній прохід:", run.Summary);
    }
}
