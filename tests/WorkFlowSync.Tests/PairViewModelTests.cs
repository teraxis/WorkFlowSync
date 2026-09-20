using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.I18n;

namespace WorkFlowSync.Tests;

[Collection("I18n Tests")]
public class PairViewModelTests
{
    public PairViewModelTests()
    {
        I18n.Instance.SetLanguage("uk");
    }
    [Fact]
    public void Round_trips_model_including_retention_and_excludes()
    {
        var model = new FolderPair
        {
            Name = "vrp",
            Source = @"E:\vrp",
            Target = @"D:\OneDrive\Робоча папка",
            Links = LinkMode.Skip,
            MaxAge = TimeSpan.FromDays(400),
            AutoClean = TimeSpan.FromDays(700),
            Exclude = { @"\Тимчасове", "*.tmp" },
        };

        var vm = PairViewModel.FromModel(model);
        Assert.True(vm.HasMaxAge);
        Assert.Equal(400, vm.MaxAgeDays);
        Assert.True(vm.HasAutoClean);
        Assert.Equal(700, vm.AutoCleanDays);
        Assert.Equal(2, vm.ExcludeCount);

        var back = vm.ToModel();
        Assert.Equal(model.Name, back.Name);
        Assert.Equal(model.Source, back.Source);
        Assert.Equal(model.Target, back.Target);
        Assert.Equal(LinkMode.Skip, back.Links);
        Assert.Equal(TimeSpan.FromDays(400), back.MaxAge);
        Assert.Equal(TimeSpan.FromDays(700), back.AutoClean);
        Assert.Equal(model.Exclude, back.Exclude);
    }

    [Fact]
    public void Unchecked_windows_mean_no_filter_and_no_cleanup_and_blank_exclude_lines_are_dropped()
    {
        var vm = new PairViewModel { Name = "a", Source = "x", Target = "y", HasMaxAge = false, HasAutoClean = false, ExcludeText = "\r\n  *.bak \r\n\r\n" };
        var m = vm.ToModel();
        Assert.Null(m.MaxAge);
        Assert.Null(m.AutoClean);
        Assert.Equal(new[] { "*.bak" }, m.Exclude);
    }

    [Fact]
    public void MainViewModel_saves_and_reloads_config_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"wfs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            var vm = new MainViewModel(path);
            Assert.True(File.Exists(path));                      // portable: created next to the app on first start
            Assert.Empty(SyncConfig.Load(path).Pairs);
            vm.AddPair(new PairViewModel { Name = "p1", Source = @"C:\src", Target = @"C:\dst", HasMaxAge = true, MaxAgeDays = 30 });
            vm.Pairs[0].IntervalMinutes = 15;   // the schedule lives on the pair now
            vm.ScanBufferKb = 512;
            vm.Background.Stop();                                // the added pair started the checker; not needed here

            // Autosave: everything above is on disk without any explicit save.
            var reloaded = new MainViewModel(path);
            reloaded.Background.Stop();
            Assert.Single(reloaded.Pairs);
            Assert.Equal("p1", reloaded.Pairs[0].Name);
            Assert.Equal(30, reloaded.Pairs[0].MaxAgeDays);
            Assert.Equal(15, reloaded.Pairs[0].IntervalMinutes);
            Assert.Equal(512, reloaded.ScanBufferKb);
            Assert.Equal("p1-2", reloaded.SuggestPairName(@"\\srv\p1"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
