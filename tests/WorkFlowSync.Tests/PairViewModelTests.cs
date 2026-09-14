using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.Tests;

public class PairViewModelTests
{
    [Fact]
    public void Round_trips_model_including_retention_and_excludes()
    {
        var model = new FolderPair
        {
            Name = "vrp",
            Source = @"E:\vrp",
            Target = @"D:\OneDrive\Робоча папка",
            Links = LinkMode.Skip,
            Retention = TimeSpan.FromDays(400),
            Exclude = { @"\Тимчасове", "*.tmp" },
        };

        var vm = PairViewModel.FromModel(model);
        Assert.True(vm.HasRetention);
        Assert.Equal(400, vm.RetentionDays);
        Assert.Equal(2, vm.ExcludeCount);

        var back = vm.ToModel();
        Assert.Equal(model.Name, back.Name);
        Assert.Equal(model.Source, back.Source);
        Assert.Equal(model.Target, back.Target);
        Assert.Equal(LinkMode.Skip, back.Links);
        Assert.Equal(TimeSpan.FromDays(400), back.Retention);
        Assert.Equal(model.Exclude, back.Exclude);
    }

    [Fact]
    public void Unchecked_retention_means_keep_forever_and_blank_exclude_lines_are_dropped()
    {
        var vm = new PairViewModel { Name = "a", Source = "x", Target = "y", HasRetention = false, ExcludeText = "\r\n  *.bak \r\n\r\n" };
        var m = vm.ToModel();
        Assert.Null(m.Retention);
        Assert.Equal(new[] { "*.bak" }, m.Exclude);
    }

    [Fact]
    public void MainViewModel_saves_and_reloads_config_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wfs-test-{Guid.NewGuid():N}.json");
        try
        {
            var vm = new MainViewModel(path);
            Assert.False(File.Exists(path));
            vm.AddPair(new PairViewModel { Name = "p1", Source = @"C:\src", Target = @"C:\dst", HasRetention = true, RetentionDays = 30 });
            vm.IntervalMinutes = 15;
            vm.ScanBufferKb = 512;
            Assert.True(vm.IsDirty);

            vm.SaveCommand.Execute(null);
            Assert.False(vm.IsDirty);
            Assert.True(File.Exists(path));

            var reloaded = new MainViewModel(path);
            Assert.Single(reloaded.Pairs);
            Assert.Equal("p1", reloaded.Pairs[0].Name);
            Assert.Equal(30, reloaded.Pairs[0].RetentionDays);
            Assert.Equal(15, reloaded.IntervalMinutes);
            Assert.Equal(512, reloaded.ScanBufferKb);
            Assert.Equal("p1-2", reloaded.SuggestPairName(@"\\srv\p1"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
