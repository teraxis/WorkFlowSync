using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Tests;

public class ApprovalViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wfs-approvalvm-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly string _src;
    private readonly string _dst;
    private readonly DateTimeOffset _now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    public ApprovalViewModelTests()
    {
        _src = Path.Combine(_dir, "src");
        _dst = Path.Combine(_dir, "dst");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);
        _dbPath = Path.Combine(_dir, "state.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private ApprovalViewModel CreateModel() =>
        new(() => _dbPath, () => new Dictionary<string, (string Source, string Target)>
        {
            { "pair1", (_src, _dst) }
        });

    [Fact]
    public void Load_populates_pending_entries_and_pair_options()
    {
        using (var store = new StateStore(_dbPath))
        {
            store.Pending.Upsert(new PendingEntry
            {
                Pair = "pair1",
                Path = "test.txt",
                Change = PendingChange.Added,
                Kind = EntryKind.File,
                DstSize = 1024,
                DetectedUtc = _now
            });
        }

        var vm = CreateModel();
        vm.Load();

        Assert.Equal(1, vm.TotalCount);
        Assert.Single(vm.FilteredItems);
        Assert.Contains("pair1", vm.PairOptions);
        Assert.Equal("test.txt", vm.FilteredItems[0].RelativePath);
        Assert.Equal("Додано", vm.FilteredItems[0].ChangeTitle);
    }

    [Fact]
    public void Filtering_by_type_and_search_query()
    {
        using (var store = new StateStore(_dbPath))
        {
            store.Pending.Upsert(new PendingEntry
            {
                Pair = "pair1",
                Path = "added.txt",
                Change = PendingChange.Added,
                Kind = EntryKind.File,
                DstSize = 1024,
                DetectedUtc = _now
            });

            store.Pending.Upsert(new PendingEntry
            {
                Pair = "pair1",
                Path = "deleted.doc",
                Change = PendingChange.Deleted,
                Kind = EntryKind.File,
                SrcSize = 2048,
                DetectedUtc = _now
            });
        }

        var vm = CreateModel();
        vm.Load();

        Assert.Equal(2, vm.TotalCount);

        vm.SelectedType = "Додано";
        Assert.Single(vm.FilteredItems);
        Assert.Equal("added.txt", vm.FilteredItems[0].RelativePath);

        vm.SelectedType = "Усі зміни";
        vm.SearchText = "deleted";
        Assert.Single(vm.FilteredItems);
        Assert.Equal("deleted.doc", vm.FilteredItems[0].RelativePath);
    }

    [Fact]
    public void Selection_updates_summary_text()
    {
        using (var store = new StateStore(_dbPath))
        {
            store.Pending.Upsert(new PendingEntry
            {
                Pair = "pair1",
                Path = "file1.txt",
                Change = PendingChange.Added,
                Kind = EntryKind.File,
                DstSize = 1048576, // 1 MB
                DetectedUtc = _now
            });
        }

        var vm = CreateModel();
        vm.Load();

        vm.SelectAll(true);

        Assert.Equal(1, vm.SelectedCount);
        Assert.Contains("буде скопійовано 1 MB", vm.SummaryText);
    }

    [Fact]
    public async Task ExecuteDecisionForItemsAsync_applies_decision()
    {
        File.WriteAllText(Path.Combine(_dst, "newfile.txt"), "hello target");

        long id;
        using (var store = new StateStore(_dbPath))
        {
            id = store.Pending.Upsert(new PendingEntry
            {
                Pair = "pair1",
                Path = "newfile.txt",
                Change = PendingChange.Added,
                Kind = EntryKind.File,
                DstSize = 12,
                DetectedUtc = _now
            });
        }

        var vm = CreateModel();
        vm.Load();

        var res = await vm.ExecuteDecisionForItemsAsync(vm.FilteredItems, ApprovalViewModel.DecisionKind.Approve);

        Assert.Equal(1, res.ApprovedCount);
        Assert.True(File.Exists(Path.Combine(_src, "newfile.txt")));
        Assert.Equal(0, vm.TotalCount);
    }

    [Fact]
    public void MultiCheckbox_filtering_allows_custom_combinations()
    {
        using (var store = new StateStore(_dbPath))
        {
            store.Pending.Upsert(new PendingEntry
            {
                Pair = "pair1",
                Path = "added.txt",
                Change = PendingChange.Added,
                Kind = EntryKind.File,
                DstSize = 100,
                DetectedUtc = _now
            });
            store.Pending.Upsert(new PendingEntry
            {
                Pair = "pair1",
                Path = "mod.txt",
                Change = PendingChange.Modified,
                Kind = EntryKind.File,
                SrcSize = 50,
                DstSize = 100,
                DetectedUtc = _now
            });
            store.Pending.Upsert(new PendingEntry
            {
                Pair = "pair1",
                Path = "del.txt",
                Change = PendingChange.Deleted,
                Kind = EntryKind.File,
                SrcSize = 20,
                DetectedUtc = _now
            });
        }

        var vm = CreateModel();
        vm.Load();

        Assert.Equal(3, vm.TotalCount);
        Assert.Equal(1, vm.AddedCount);
        Assert.Equal(1, vm.ModifiedCount);
        Assert.Equal(1, vm.DeletedCount);

        // Turn off modified
        vm.FilterModified = false;
        Assert.Equal(2, vm.FilteredItems.Count);
        Assert.Contains(vm.FilteredItems, i => i.RelativePath == "added.txt");
        Assert.Contains(vm.FilteredItems, i => i.RelativePath == "del.txt");

        // Turn off added, keep only deleted
        vm.FilterAdded = false;
        Assert.Single(vm.FilteredItems);
        Assert.Equal("del.txt", vm.FilteredItems[0].RelativePath);

        // Turn off all
        vm.FilterDeleted = false;
        Assert.Empty(vm.FilteredItems);
    }

    [Fact]
    public void Absent_and_date_formatting_matches_requirements()
    {
        var testDate = new DateTimeOffset(2026, 9, 19, 14, 30, 45, TimeSpan.Zero);
        using (var store = new StateStore(_dbPath))
        {
            store.Pending.Upsert(new PendingEntry
            {
                Pair = "pair1",
                Path = "added.txt",
                Change = PendingChange.Added,
                Kind = EntryKind.File,
                DstSize = 100,
                DstMtimeUtc = testDate,
                DetectedUtc = testDate
            });
            store.Pending.Upsert(new PendingEntry
            {
                Pair = "pair1",
                Path = "del.txt",
                Change = PendingChange.Deleted,
                Kind = EntryKind.File,
                SrcSize = 20,
                SrcMtimeUtc = testDate,
                DetectedUtc = testDate
            });
        }

        var vm = CreateModel();
        vm.Load();

        var addedItem = vm.Items.First(i => i.RelativePath == "added.txt");
        Assert.True(addedItem.IsWasAbsent);
        Assert.Equal("відсутнє", addedItem.WasPath);
        Assert.Equal(testDate.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"), addedItem.BecameMtime);

        var delItem = vm.Items.First(i => i.RelativePath == "del.txt");
        Assert.True(delItem.IsBecameAbsent);
        Assert.Equal("відсутнє", delItem.BecamePath);
        Assert.Equal(testDate.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"), delItem.WasMtime);
    }

    [Fact]
    public void PairFilter_multiselect_dropdown_filters_and_summarizes()
    {
        using (var store = new StateStore(_dbPath))
        {
            store.Pending.Upsert(new PendingEntry
            {
                Pair = "pair1",
                Path = "p1.txt",
                Change = PendingChange.Added,
                Kind = EntryKind.File,
                DstSize = 100,
                DetectedUtc = _now
            });
            store.Pending.Upsert(new PendingEntry
            {
                Pair = "pair2",
                Path = "p2.txt",
                Change = PendingChange.Added,
                Kind = EntryKind.File,
                DstSize = 200,
                DetectedUtc = _now
            });
        }

        var vm = CreateModel();
        vm.Load();

        Assert.Equal(2, vm.PairFilters.Count);
        Assert.Equal(2, vm.FilteredItems.Count);
        Assert.True(vm.IsAllPairsSelected);

        // Turn off pair1 (indeterminate state)
        vm.PairFilters.First(p => p.Name == "pair1").IsSelected = false;
        Assert.Null(vm.IsAllPairsSelected);
        Assert.Single(vm.FilteredItems);
        Assert.Equal("p2.txt", vm.FilteredItems[0].RelativePath);
        Assert.Equal("pair2", vm.SelectedPairsSummary);

        // Turn off all
        vm.IsAllPairsSelected = false;
        Assert.False(vm.IsAllPairsSelected);
        Assert.Empty(vm.FilteredItems);

        // Turn all back on
        vm.IsAllPairsSelected = true;
        Assert.Equal(2, vm.FilteredItems.Count);
        Assert.True(vm.PairFilters.All(p => p.IsSelected));
    }

    [Fact]
    public void SelectedTypesSummary_and_IsAllTypesSelected_sync()
    {
        var vm = CreateModel();
        vm.Load();

        Assert.True(vm.IsAllTypesSelected);
        Assert.Equal("Усі зміни", vm.SelectedTypesSummary);

        // Deselect all
        vm.IsAllTypesSelected = false;
        Assert.False(vm.FilterAdded);
        Assert.False(vm.FilterModified);
        Assert.False(vm.FilterDeleted);
        Assert.False(vm.FilterRenamed);
        Assert.Equal("Не обрано", vm.SelectedTypesSummary);

        // Select only Added
        vm.FilterAdded = true;
        Assert.Equal("Додано", vm.SelectedTypesSummary);

        // Select Added and Deleted
        vm.FilterDeleted = true;
        Assert.Contains("Обрано: 2", vm.SelectedTypesSummary);

        // Select all types
        vm.IsAllTypesSelected = true;
        Assert.True(vm.FilterAdded);
        Assert.True(vm.FilterModified);
        Assert.True(vm.FilterDeleted);
        Assert.True(vm.FilterRenamed);
        Assert.Equal("Усі зміни", vm.SelectedTypesSummary);
    }

    [Fact]
    public void FilteredGroups_populates_paths_and_handles_selection()
    {
        using (var store = new StateStore(_dbPath))
        {
            store.Pending.Upsert(new PendingEntry
            {
                Pair = "pair1",
                Path = "item1.txt",
                Change = PendingChange.Added,
                Kind = EntryKind.File,
                DstSize = 100,
                DetectedUtc = _now
            });
            store.Pending.Upsert(new PendingEntry
            {
                Pair = "pair1",
                Path = "item2.txt",
                Change = PendingChange.Modified,
                Kind = EntryKind.File,
                SrcSize = 50,
                DstSize = 100,
                DetectedUtc = _now
            });
        }

        var vm = CreateModel();
        vm.Load();

        var group = Assert.Single(vm.FilteredGroups);
        Assert.Equal("pair1", group.PairName);
        Assert.Equal(_src, group.SourceRootDisplay);
        Assert.Equal(_dst, group.TargetRootDisplay);
        Assert.Equal(2, group.Count);
        Assert.False(group.IsAllSelected);

        // Select all in group
        group.IsAllSelected = true;
        Assert.True(group.Items.All(i => i.IsSelected));

        // Deselect one item
        group.Items[0].IsSelected = false;
        Assert.Null(group.IsAllSelected);
    }
}
