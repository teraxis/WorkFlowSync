using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core;
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
            Name = "docs",
            Source = @"E:\docs",
            Target = @"D:\Sync\docs",
            Links = LinkMode.Skip,
            MaxAge = TimeSpan.FromDays(400),
            AutoClean = TimeSpan.FromDays(700),
            KeepLatestFiles = 100,
            FreeUpSpaceAfterCopy = true,
            FreeUpSourceSpaceAfterCopy = true,
            DiskQuotaEnabled = true,
            MinFreeSpaceGb = 12,
            RotateOnLowSpace = true,
            Exclude = { @"\Тимчасове", "*.tmp" },
        };

        var vm = PairViewModel.FromModel(model);
        Assert.True(vm.HasMaxAge);
        Assert.Equal(400, vm.MaxAgeDays);
        Assert.True(vm.HasAutoClean);
        Assert.Equal(700, vm.AutoCleanDays);
        Assert.True(vm.HasKeepLatestFiles);
        Assert.Equal(100, vm.KeepLatestFilesCount);
        Assert.True(vm.FreeUpSpaceAfterCopy);
        Assert.True(vm.FreeUpSourceSpaceAfterCopy);
        Assert.True(vm.DiskQuotaEnabled);
        Assert.Equal(12, vm.MinFreeSpaceGb);
        Assert.True(vm.RotateOnLowSpace);
        Assert.Equal(2, vm.ExcludeCount);

        var back = vm.ToModel();
        Assert.Equal(model.Name, back.Name);
        Assert.Equal(model.Source, back.Source);
        Assert.Equal(model.Target, back.Target);
        Assert.Equal(LinkMode.Skip, back.Links);
        Assert.Equal(TimeSpan.FromDays(400), back.MaxAge);
        Assert.Equal(TimeSpan.FromDays(700), back.AutoClean);
        Assert.Equal(100, back.KeepLatestFiles);
        Assert.True(back.FreeUpSpaceAfterCopy);
        Assert.True(back.FreeUpSourceSpaceAfterCopy);
        Assert.True(back.DiskQuotaEnabled);
        Assert.Equal(12, back.MinFreeSpaceGb);
        Assert.True(back.RotateOnLowSpace);
        Assert.Equal(model.Exclude, back.Exclude);
    }

    [Fact]
    public void UploadConvertedDocs_RoundTrips_And_GatedByMode()
    {
        var model = new FolderPair
        {
            Name = "gdrive-pair",
            Source = @"gdrive://user@gmail.com/Reports?id=123&acc=456",
            Target = @"D:\LocalDocs",
            Mode = SyncMode.TwoWay,
            GoogleDocExport = GoogleDocExportMode.Office,
            UploadConvertedDocs = true,
        };

        var vm = PairViewModel.FromModel(model);
        Assert.True(vm.GoogleDocExportApplies);
        Assert.True(vm.CanUploadConvertedDocs);
        Assert.True(vm.UploadConvertedDocs);

        var back = vm.ToModel();
        Assert.True(back.UploadConvertedDocs);

        // In Mirror mode, upload is not applicable (source is read-only)
        vm.Mode = SyncMode.Mirror;
        Assert.False(vm.CanUploadConvertedDocs);
    }

    [Fact]
    public void Unchecked_windows_mean_no_filter_and_no_cleanup_and_blank_exclude_lines_are_dropped()
    {
        var vm = new PairViewModel { Name = "a", Source = "x", Target = "y", HasMaxAge = false, HasAutoClean = false, HasKeepLatestFiles = false, ExcludeText = "\r\n  *.bak \r\n\r\n" };
        var m = vm.ToModel();
        Assert.Null(m.MaxAge);
        Assert.Null(m.AutoClean);
        Assert.Null(m.KeepLatestFiles);
        Assert.Equal(new[] { "*.bak" }, m.Exclude);
    }

    [Fact]
    public void Keep_latest_files_is_mirror_only()
    {
        var vm = new PairViewModel { Name = "a", Source = "x", Target = "y", Mode = SyncMode.TwoWay, HasKeepLatestFiles = true, KeepLatestFilesCount = 20 };
        Assert.Null(vm.ToModel().KeepLatestFiles);

        vm.Mode = SyncMode.Mirror;
        Assert.Equal(20, vm.ToModel().KeepLatestFiles);
    }

    [Fact]
    public void Clean_existing_files_and_has_any_cleanup()
    {
        var vm = new PairViewModel { Name = "a", Source = "x", Target = "y", Mode = SyncMode.Mirror };
        Assert.False(vm.HasAnyCleanup);

        vm.HasAutoClean = true;
        Assert.True(vm.HasAnyCleanup);

        vm.CleanExistingFiles = true;
        var m = vm.ToModel();
        Assert.True(m.CleanExistingFiles);
        Assert.Null(m.CleanupFromUtc);

        vm.CleanExistingFiles = false;
        var m2 = vm.ToModel();
        Assert.False(m2.CleanExistingFiles);
        Assert.NotNull(m2.CleanupFromUtc);

        // In TwoWay mode, cleanup is disabled
        vm.Mode = SyncMode.TwoWay;
        Assert.False(vm.HasAnyCleanup);
        Assert.False(vm.ToModel().CleanExistingFiles);
    }

    [Fact]
    public void Free_space_controls_require_a_registered_local_cloud_sync_root()
    {
        var local = Path.GetTempPath();

        Assert.True(new PairViewModel(new FixedCloudPlatform(true)) { Target = local }.FreeUpSpaceApplies);
        Assert.False(new PairViewModel(new FixedCloudPlatform(false)) { Target = local }.FreeUpSpaceApplies);
        Assert.False(new PairViewModel(new FixedCloudPlatform(true)) { Target = @"\\server\share" }.FreeUpSpaceApplies);

        Assert.True(new PairViewModel(new FixedCloudPlatform(true)) { Source = local }.FreeUpSourceSpaceApplies);
        Assert.False(new PairViewModel(new FixedCloudPlatform(false)) { Source = local }.FreeUpSourceSpaceApplies);
        Assert.False(new PairViewModel(new FixedCloudPlatform(true)) { Source = @"\\server\share" }.FreeUpSourceSpaceApplies);
    }

    [Fact]
    public void Rotation_requires_quota_and_accepts_any_target_storage_kind()
    {
        var local = Path.GetTempPath();
        var ordinary = new PairViewModel(new FixedCloudPlatform(false)) { Target = local };

        Assert.True(ordinary.DiskQuotaApplies);
        Assert.False(ordinary.RotationApplies);
        ordinary.DiskQuotaEnabled = true;
        Assert.True(ordinary.RotationApplies);

        var cloud = new PairViewModel(new FixedCloudPlatform(true)) { Target = local, DiskQuotaEnabled = true };
        Assert.True(cloud.RotationApplies);

        var network = new PairViewModel(new FixedCloudPlatform(false))
            { Target = @"\\server\share", DiskQuotaEnabled = true };
        Assert.True(network.DiskQuotaApplies);
        Assert.True(network.RotationApplies);
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
            vm.AddPair(new PairViewModel { Name = "p1", Source = @"C:\src", Target = @"C:\dst", HasMaxAge = true, MaxAgeDays = 30, IntervalMinutes = 15 });
            vm.Background.Stop();
            vm.ScanBufferKb = 512;
            vm.SaveNow();
            // checker already stopped above

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

    [Fact]
    public void Unsynced_pair_shows_pending_status_and_not_up_to_date()
    {
        var vm = new PairViewModel { Name = "new", Source = "src", Target = "tgt", Enabled = true, LastSyncTime = null };
        Assert.False(vm.IsUpToDate);
        Assert.Equal("Очікує синхронізації", vm.SyncStatusTitle);
        Assert.Equal("Ще не синхронізувалося", vm.SyncStatusSubtext);
    }

    [Fact]
    public void MainViewModel_TaskFilter_DefaultsToActiveOnly_AndHidesInactiveTasks()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"wfs-filter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            var vm = new MainViewModel(path);
            vm.Background.Stop();

            // Default: ShowActiveTasksOnly == true
            Assert.True(vm.ShowActiveTasksOnly);
            Assert.False(vm.ShowAllTasks);

            var p1 = new PairViewModel { Name = "active-task", Source = @"C:\s1", Target = @"C:\t1", Enabled = true };
            var p2 = new PairViewModel { Name = "paused-task", Source = @"C:\s2", Target = @"C:\t2", Enabled = false };

            vm.AddPair(p1);
            vm.AddPair(p2);

            Assert.Equal(2, vm.Pairs.Count);
            Assert.Single(vm.VisiblePairs);
            Assert.Equal("active-task", vm.VisiblePairs[0].Name);
            Assert.True(vm.HasVisiblePairs);
            Assert.False(vm.ShowEmptyFilteredState);

            // Switch to All tasks
            vm.ShowAllTasks = true;
            Assert.False(vm.ShowActiveTasksOnly);
            Assert.Equal(2, vm.VisiblePairs.Count);

            // Switch back to Active only
            vm.ShowActiveTasksOnly = true;
            Assert.Single(vm.VisiblePairs);
            Assert.Equal("active-task", vm.VisiblePairs[0].Name);

            // Disabling the active task makes it disappear immediately from VisiblePairs
            p1.Enabled = false;
            Assert.Empty(vm.VisiblePairs);
            Assert.False(vm.HasVisiblePairs);
            Assert.True(vm.ShowEmptyFilteredState);

            // Re-enabling makes it reappear immediately
            p1.Enabled = true;
            Assert.Single(vm.VisiblePairs);
            Assert.Equal("active-task", vm.VisiblePairs[0].Name);
            Assert.False(vm.ShowEmptyFilteredState);

            // ShowAllTasksActionCommand resets filter
            p1.Enabled = false;
            Assert.True(vm.ShowEmptyFilteredState);
            vm.ShowAllTasksActionCommand.Execute(null);
            Assert.True(vm.ShowAllTasks);
            Assert.Equal(2, vm.VisiblePairs.Count);
            Assert.False(vm.ShowEmptyFilteredState);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Sync_progress_is_reported_with_current_total_and_percentage_in_statuses()
    {
        var vm = new PairViewModel { Name = "test-task", Source = @"C:\s", Target = @"C:\t", Enabled = true };
        vm.IsBusy = true;

        // Before progress: default processing state
        Assert.Equal("Синхронізація…", vm.SyncStatusTitle);
        Assert.Equal("Порівняння файлів…", vm.SyncStatusSubtext);
        Assert.Equal("Синхронізація", vm.StatusBadgeText);

        // Progress update: 3 of 10 (30%)
        vm.UpdateProgress(3, 10);
        Assert.Equal(3, vm.SyncCurrentFiles);
        Assert.Equal(10, vm.SyncTotalFiles);
        Assert.Equal(30, vm.SyncProgressPercent);
        Assert.Equal("Синхронізація…", vm.SyncStatusTitle);
        Assert.Equal("Синхронізовано 3 з 10 (30%)", vm.SyncStatusSubtext);
        Assert.Equal("Синхронізація", vm.StatusBadgeText);
        Assert.Contains("3/10, 30%", vm.StateSummary);

        // Reset progress on finish
        vm.ResetProgress();
        Assert.Equal(0, vm.SyncCurrentFiles);
        Assert.Equal(0, vm.SyncTotalFiles);
        Assert.Equal(0, vm.SyncProgressPercent);
        Assert.Equal("Синхронізація…", vm.SyncStatusTitle);
        Assert.Equal("Порівняння файлів…", vm.SyncStatusSubtext);
        Assert.Equal("Синхронізація", vm.StatusBadgeText);
    }

    [Fact]
    public void SyncExecutor_reports_progress_at_start_and_after_each_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"wfs-prog-{Guid.NewGuid():N}");
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);
        try
        {
            var plan = new WorkFlowSync.Core.Planning.SyncPlan();
            for (var i = 0; i < 3; i++)
            {
                var name = $"prog{i}.txt";
                File.WriteAllText(Path.Combine(src, name), "content");
                plan.Actions.Add(new WorkFlowSync.Core.Planning.SyncAction(
                    WorkFlowSync.Core.Planning.SyncActionKind.CopyFile,
                    name,
                    new WorkFlowSync.Core.Model.StateEntry
                    {
                        PairName = "p",
                        RelativePath = name,
                        Kind = WorkFlowSync.Core.Model.EntryKind.File,
                        Status = WorkFlowSync.Core.Model.EntryStatus.Active,
                        FirstSeenUtc = DateTimeOffset.UtcNow,
                        LastSeenUtc = DateTimeOffset.UtcNow,
                        SourceSize = 7
                    }));
            }

            var progressReports = new List<(int Current, int Total)>();
            var executor = new WorkFlowSync.Core.Execution.SyncExecutor(src, dst, new WorkFlowSync.Core.Logging.MemorySyncLog(), dryRun: false);
            executor.Execute(plan, "[p]", progress: (cur, tot) => progressReports.Add((cur, tot)));

            Assert.Contains((0, 3), progressReports);
            Assert.Contains((1, 3), progressReports);
            Assert.Contains((2, 3), progressReports);
            Assert.Contains((3, 3), progressReports);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private sealed class FixedCloudPlatform(bool isSyncRoot) : ICloudFilePlatform
    {
        public bool IsSyncRoot(string path) => isSyncRoot;
        public CloudDiskSpace GetDiskSpace(string path) => new(10, 20);
        public void RequestOnlineOnly(string path) { }
    }
}
