using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using Xunit;

namespace WorkFlowSync.Tests;

public sealed class AboutPageTests
{
    [Fact]
    public void WorkFlowSyncInfo_ContainsAuthorAndMetadata()
    {
        Assert.Equal("WorkFlowSync", WorkFlowSyncInfo.Name);
        Assert.False(string.IsNullOrWhiteSpace(WorkFlowSyncInfo.Version));
        Assert.Equal("Білик Ігор", WorkFlowSyncInfo.Author);
        Assert.Equal("Bilyk Ihor", WorkFlowSyncInfo.AuthorEnglish);
        Assert.Equal("teraxis@gmail.com", WorkFlowSyncInfo.AuthorEmail);
        Assert.Equal("https://github.com/teraxis/WorkFlowSync", WorkFlowSyncInfo.GitHubRepo);
        Assert.Equal("https://github.com/teraxis", WorkFlowSyncInfo.GitHubAuthor);
        Assert.Equal("MIT License", WorkFlowSyncInfo.License);
        Assert.Contains("Білик Ігор", WorkFlowSyncInfo.Copyright);
    }

    [Fact]
    public void MainViewModel_SelectedPage_HandlesAboutPage()
    {
        var tempConfig = Path.Combine(Path.GetTempPath(), $"wfs_about_test_{Guid.NewGuid():N}.json");
        try
        {
            ConfigFile.Save(new SyncConfig(), tempConfig);
            var vm = new MainViewModel(tempConfig);
            vm.Background.Stop();

            // Default is tasks (0)
            Assert.Equal(0, vm.SelectedPage);
            Assert.True(vm.IsFoldersPage);
            Assert.False(vm.IsAboutPage);

            // Switch to about page (4)
            vm.SelectedPage = 4;
            Assert.True(vm.IsAboutPage);
            Assert.False(vm.IsFoldersPage);
            Assert.False(vm.IsActivityPage);
            Assert.False(vm.IsAccountsPage);
            Assert.False(vm.IsSettingsPage);
            Assert.Equal(4, vm.SelectedPage);

            // Page title should be resolved
            Assert.False(string.IsNullOrWhiteSpace(vm.PageTitle));
        }
        finally
        {
            if (File.Exists(tempConfig)) File.Delete(tempConfig);
        }
    }
}
