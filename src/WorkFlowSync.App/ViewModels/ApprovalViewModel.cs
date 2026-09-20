using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;
using WorkFlowSync.Core.I18n;

namespace WorkFlowSync.App.ViewModels;

public sealed partial class ApprovalItemViewModel : ObservableObject
{
    public required PendingEntry Entry { get; init; }

    public long Id => Entry.Id;
    public string PairName => Entry.Pair;
    public string RelativePath => Entry.Path;
    public PendingChange Change => Entry.Change;

    public bool IsWasAbsent => Entry.Change == PendingChange.Added;

    public string WasPath => Entry.Change switch
    {
        PendingChange.Added => I18n.T("approval.absent"),
        PendingChange.Renamed or PendingChange.Moved => Entry.FromPath ?? Entry.Path,
        _ => Entry.Path
    };

    public string WasSize => Entry.SrcSize is { } sz ? SyncExecutor.FormatSize(sz) : "";
    public string WasMtime => Entry.SrcMtimeUtc is { } mt ? mt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") : "";

    public bool IsBecameAbsent => Entry.Change == PendingChange.Deleted;

    public string BecamePath => Entry.Change switch
    {
        PendingChange.Deleted => I18n.T("approval.absent"),
        _ => Entry.Path
    };

    public string BecameSize => Entry.DstSize is { } sz ? SyncExecutor.FormatSize(sz) : "";
    public string BecameMtime => Entry.DstMtimeUtc is { } mt ? mt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") : "";

    public string DetectedTimeText => Entry.DetectedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");

    public string SizeDeltaText
    {
        get
        {
            if (Entry.SrcSize is { } src && Entry.DstSize is { } dst)
            {
                var diff = dst - src;
                if (diff > 0) return $"+{SyncExecutor.FormatSize(diff)}";
                if (diff < 0) return $"−{SyncExecutor.FormatSize(Math.Abs(diff))}";
            }
            return "";
        }
    }

    public bool IsSizePositive => Entry.DstSize > Entry.SrcSize;
    public bool IsSizeNegative => Entry.DstSize < Entry.SrcSize;

    public bool IsAdded => Entry.Change == PendingChange.Added;
    public bool IsDeleted => Entry.Change == PendingChange.Deleted;
    public bool IsModified => Entry.Change == PendingChange.Modified;
    public bool IsRenamed => Entry.Change is PendingChange.Renamed or PendingChange.Moved;
    public bool IsConflict => Entry.Change == PendingChange.Conflict;

    public string ChangeTitle => Entry.Change switch
    {
        PendingChange.Added => I18n.T("approval.type_created"),
        PendingChange.Deleted => I18n.T("approval.type_deleted"),
        PendingChange.Modified => I18n.T("approval.type_modified"),
        PendingChange.Renamed => I18n.T("approval.type_renamed"),
        PendingChange.Moved => I18n.T("approval.type_renamed"),
        PendingChange.Conflict => I18n.T("approval.type_conflict"),
        _ => I18n.T("approval.type_modified"),
    };

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(ChangeTitle));
        OnPropertyChanged(nameof(WasPath));
        OnPropertyChanged(nameof(BecamePath));
    }

    public string ChangeIconKey => Entry.Change switch
    {
        PendingChange.Added => "IconPlus",
        PendingChange.Deleted => "IconTrash",
        PendingChange.Modified => "IconPencil",
        PendingChange.Renamed or PendingChange.Moved => "IconArrowRight",
        PendingChange.Conflict => "IconShield",
        _ => "IconPencil",
    };

    public string AgoText => RecentChangesViewModel.Humanise(Entry.DetectedUtc);

    public bool HasSizeDelta => Entry.SrcSize.HasValue && Entry.DstSize.HasValue && Entry.SrcSize != Entry.DstSize;
    public bool IsMtimeChanged => Entry.SrcMtimeUtc.HasValue && Entry.DstMtimeUtc.HasValue && Entry.SrcMtimeUtc != Entry.DstMtimeUtc;
    public string ActionDirectionTooltip => Entry.Change switch
    {
        PendingChange.Conflict => I18n.T("approval.direction_conflict", "Конфлікт: погодження запише версію у Першу теку (A)"),
        _ => I18n.T("approval.direction_to_source", "Напрямок дії: зміни буде застосовано до Першої теки (A)")
    };

    [ObservableProperty] private bool _isSelected;
}

public sealed partial class ApprovalGroupViewModel : ObservableObject
{
    public required string PairName { get; init; }
    public required string SourceRoot { get; init; }
    public required string TargetRoot { get; init; }
    public ObservableCollection<ApprovalItemViewModel> Items { get; } = new();

    public int Count => Items.Count;
    public string CountSummary => $"{Items.Count} {GetItemsLabel(Items.Count)}";

    public string SourceRootDisplay => string.IsNullOrWhiteSpace(SourceRoot) ? I18n.T("approval.source_default", "Джерело") : SourceRoot;
    public string TargetRootDisplay => string.IsNullOrWhiteSpace(TargetRoot) ? I18n.T("approval.target_default", "Ціль") : TargetRoot;

    [ObservableProperty] private bool? _isAllSelected = false;

    private bool _suppressSync;

    public void UpdateSelectionState()
    {
        if (_suppressSync) return;
        _suppressSync = true;
        try
        {
            if (Items.Count == 0)
                IsAllSelected = false;
            else if (Items.All(i => i.IsSelected))
                IsAllSelected = true;
            else if (Items.All(i => !i.IsSelected))
                IsAllSelected = false;
            else
                IsAllSelected = null;
        }
        finally
        {
            _suppressSync = false;
        }
    }

    partial void OnIsAllSelectedChanged(bool? value)
    {
        if (_suppressSync || value is null) return;
        _suppressSync = true;
        try
        {
            foreach (var item in Items)
            {
                item.IsSelected = value.Value;
            }
        }
        finally
        {
            _suppressSync = false;
        }
    }

    private static string GetItemsLabel(int n)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        if (mod100 is >= 11 and <= 19) return "змін";
        if (mod10 == 1) return "зміна";
        if (mod10 is >= 2 and <= 4) return "зміни";
        return "змін";
    }
}

public sealed partial class PairFilterItemViewModel : ObservableObject
{
    public required string Name { get; init; }
    [ObservableProperty] private bool _isSelected = true;
    public Action? OnChanged { get; set; }
    partial void OnIsSelectedChanged(bool value) => OnChanged?.Invoke();
}

public sealed partial class ApprovalViewModel : ObservableObject
{
    public enum DecisionKind { Approve, Dismiss, Revert }

    private readonly Func<string> _statePath;
    private readonly Func<IReadOnlyDictionary<string, (string Source, string Target)>> _pairRoots;
    private readonly Action<string> _log;

    public ApprovalViewModel(
        Func<string> statePath,
        Func<IReadOnlyDictionary<string, (string Source, string Target)>> pairRoots,
        Action<string>? log = null)
    {
        _statePath = statePath;
        _pairRoots = pairRoots;
        _log = log ?? (_ => { });

        I18n.Instance.LanguageChanged += () =>
        {
            RefreshTypeOptions();
            foreach (var item in Items)
            {
                item.RefreshLanguage();
            }
            UpdateSummary();
            NotifyCountsChanged();
            UpdatePairsSummary();
            UpdateTypesSummary();
        };

        PairOptions.Add(I18n.T("approval.all_pairs"));
        RefreshTypeOptions();
        _selectedType = TypeOptions[0];
        _selectedPair = PairOptions[0];
    }

    private void RefreshTypeOptions()
    {
        var prevIdx = SelectedTypeIndex;
        TypeOptions.Clear();
        TypeOptions.Add(I18n.T("approval.all_types"));
        TypeOptions.Add(I18n.T("approval.type_created"));
        TypeOptions.Add(I18n.T("approval.type_deleted"));
        TypeOptions.Add(I18n.T("approval.type_modified"));
        TypeOptions.Add(I18n.T("approval.type_renamed"));
        TypeOptions.Add(I18n.T("approval.type_conflict"));

        if (prevIdx >= 0 && prevIdx < TypeOptions.Count)
            SelectedType = TypeOptions[prevIdx];
        else
            SelectedType = TypeOptions[0];
    }

    public int SelectedTypeIndex => TypeOptions.IndexOf(SelectedType);

    public ObservableCollection<ApprovalItemViewModel> Items { get; } = new();
    public ObservableCollection<ApprovalItemViewModel> FilteredItems { get; } = new();
    public ObservableCollection<ApprovalGroupViewModel> FilteredGroups { get; } = new();

    public ObservableCollection<string> PairOptions { get; } = new();
    public ObservableCollection<string> TypeOptions { get; } = new();
    public ObservableCollection<PairFilterItemViewModel> PairFilters { get; } = new();

    [ObservableProperty] private string _selectedPair;
    [ObservableProperty] private string _selectedType;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _hasUnloadedChanges;
    [ObservableProperty] private int _unloadedCount;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private bool _isCeilingExceeded;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private bool? _isAllPairsSelected = true;
    [ObservableProperty] private bool? _isAllTypesSelected = true;

    [ObservableProperty] private bool _filterAdded = true;
    [ObservableProperty] private bool _filterModified = true;
    [ObservableProperty] private bool _filterDeleted = true;
    [ObservableProperty] private bool _filterRenamed = true;
    [ObservableProperty] private bool _filterConflict = true;

    private bool _suppressPairSync;

    partial void OnIsAllPairsSelectedChanged(bool? value)
    {
        if (_suppressPairSync || value is null) return;
        _suppressPairSync = true;
        try
        {
            var target = value.Value;
            foreach (var p in PairFilters)
            {
                p.IsSelected = target;
            }
        }
        finally
        {
            _suppressPairSync = false;
        }
        UpdatePairsSummary();
        ApplyFilter();
    }

    private void OnPairFilterItemChanged()
    {
        if (_suppressPairSync) return;
        _suppressPairSync = true;
        try
        {
            if (PairFilters.Count == 0)
                IsAllPairsSelected = true;
            else if (PairFilters.All(p => p.IsSelected))
                IsAllPairsSelected = true;
            else if (PairFilters.All(p => !p.IsSelected))
                IsAllPairsSelected = false;
            else
                IsAllPairsSelected = null;
        }
        finally
        {
            _suppressPairSync = false;
        }
        UpdatePairsSummary();
        ApplyFilter();
    }

    private bool _suppressTypeSync;

    partial void OnIsAllTypesSelectedChanged(bool? value)
    {
        if (_suppressTypeSync || value is null) return;
        _suppressTypeSync = true;
        try
        {
            var target = value.Value;
            FilterAdded = target;
            FilterModified = target;
            FilterDeleted = target;
            FilterRenamed = target;
            FilterConflict = target;
        }
        finally
        {
            _suppressTypeSync = false;
        }
        UpdateTypesSummary();
        ApplyFilter();
    }

    private void OnIndividualTypeChanged()
    {
        if (_suppressTypeSync) return;
        _suppressTypeSync = true;
        try
        {
            var types = new[] { FilterAdded, FilterModified, FilterDeleted, FilterRenamed };
            var allSelected = types.All(t => t) && (ConflictCount == 0 || FilterConflict);
            var noneSelected = types.All(t => !t) && (ConflictCount == 0 || !FilterConflict);

            if (allSelected)
                IsAllTypesSelected = true;
            else if (noneSelected)
                IsAllTypesSelected = false;
            else
                IsAllTypesSelected = null;
        }
        finally
        {
            _suppressTypeSync = false;
        }
        UpdateTypesSummary();
        ApplyFilter();
    }

    partial void OnFilterAddedChanged(bool value) => OnIndividualTypeChanged();
    partial void OnFilterModifiedChanged(bool value) => OnIndividualTypeChanged();
    partial void OnFilterDeletedChanged(bool value) => OnIndividualTypeChanged();
    partial void OnFilterRenamedChanged(bool value) => OnIndividualTypeChanged();
    partial void OnFilterConflictChanged(bool value) => OnIndividualTypeChanged();

    public string SelectedPairsSummary
    {
        get
        {
            if (PairFilters.Count == 0) return I18n.T("approval.all_pairs");
            var selected = PairFilters.Where(p => p.IsSelected).ToList();
            if (selected.Count == PairFilters.Count) return I18n.T("approval.all_pairs");
            if (selected.Count == 0) return I18n.T("approval.none_selected");
            if (selected.Count == 1) return selected[0].Name;
            return $"{I18n.T("approval.selected_prefix")}: {selected.Count}";
        }
    }

    public string SelectedTypesSummary
    {
        get
        {
            var active = new List<string>();
            if (FilterAdded) active.Add(I18n.T("approval.type_created"));
            if (FilterModified) active.Add(I18n.T("approval.type_modified"));
            if (FilterDeleted) active.Add(I18n.T("approval.type_deleted"));
            if (FilterRenamed) active.Add(I18n.T("approval.type_renamed"));
            if (FilterConflict && ConflictCount > 0) active.Add(I18n.T("approval.type_conflict"));

            int totalTypes = ConflictCount > 0 ? 5 : 4;
            if (active.Count >= totalTypes) return I18n.T("approval.all_types");
            if (active.Count == 0) return I18n.T("approval.none_selected");
            if (active.Count == 1) return active[0];
            return $"{I18n.T("approval.selected_prefix")}: {active.Count}";
        }
    }

    public void UpdatePairsSummary() => OnPropertyChanged(nameof(SelectedPairsSummary));
    public void UpdateTypesSummary() => OnPropertyChanged(nameof(SelectedTypesSummary));

    public int TotalCount => Items.Count;
    public int SelectedCount => Items.Count(i => i.IsSelected);
    public bool HasItems => Items.Count > 0;

    public int AddedCount => Items.Count(i => i.IsAdded);
    public int ModifiedCount => Items.Count(i => i.IsModified);
    public int DeletedCount => Items.Count(i => i.IsDeleted);
    public int RenamedCount => Items.Count(i => i.IsRenamed);
    public int ConflictCount => Items.Count(i => i.IsConflict);

    public string AddedFilterTitle => $"{I18n.T("approval.type_created")} ({AddedCount})";
    public string ModifiedFilterTitle => $"{I18n.T("approval.type_modified")} ({ModifiedCount})";
    public string DeletedFilterTitle => $"{I18n.T("approval.type_deleted")} ({DeletedCount})";
    public string RenamedFilterTitle => $"{I18n.T("approval.type_renamed")} ({RenamedCount})";

    public bool HasConflicts => ConflictCount > 0;

    public void NotifyCountsChanged()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(AddedCount));
        OnPropertyChanged(nameof(ModifiedCount));
        OnPropertyChanged(nameof(DeletedCount));
        OnPropertyChanged(nameof(RenamedCount));
        OnPropertyChanged(nameof(ConflictCount));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(AddedFilterTitle));
        OnPropertyChanged(nameof(ModifiedFilterTitle));
        OnPropertyChanged(nameof(DeletedFilterTitle));
        OnPropertyChanged(nameof(RenamedFilterTitle));
        UpdatePairsSummary();
        UpdateTypesSummary();
    }

    public void Load()
    {
        Items.Clear();
        PairOptions.Clear();
        PairFilters.Clear();
        var allPairsLabel = I18n.T("approval.all_pairs");
        PairOptions.Add(allPairsLabel);

        try
        {
            using var store = new StateStore(_statePath());
            var open = store.Pending.GetOpen();

            var pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in open)
            {
                pairs.Add(entry.Pair);
                var vm = new ApprovalItemViewModel { Entry = entry };
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ApprovalItemViewModel.IsSelected))
                    {
                        UpdateSummary();
                        OnPropertyChanged(nameof(SelectedCount));
                    }
                };
                Items.Add(vm);
            }

            foreach (var p in pairs.OrderBy(p => p))
            {
                PairOptions.Add(p);
                var filter = new PairFilterItemViewModel { Name = p, IsSelected = true };
                filter.OnChanged = OnPairFilterItemChanged;
                PairFilters.Add(filter);
            }

            SelectedPair = allPairsLabel;
            IsAllPairsSelected = true;
            IsAllTypesSelected = true;
            FilterAdded = true;
            FilterModified = true;
            FilterDeleted = true;
            FilterRenamed = true;
            FilterConflict = true;

            HasUnloadedChanges = false;
            UnloadedCount = 0;
            IsCeilingExceeded = Items.Count > 100;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Помилка завантаження: {ex.Message}";
        }

        NotifyCountsChanged();
        ApplyFilter();
    }

    partial void OnSelectedPairChanged(string value)
    {
        if (PairFilters.Count > 0 && !string.IsNullOrEmpty(value))
        {
            var isAll = (PairOptions.Count > 0 && value == PairOptions[0]) ||
                        value == "Усі пари" || value == "Усі завдання" ||
                        value == "All pairs" || value == "All tasks";
            if (isAll)
            {
                IsAllPairsSelected = true;
            }
            else
            {
                foreach (var pf in PairFilters)
                {
                    pf.IsSelected = pf.Name.Equals(value, StringComparison.OrdinalIgnoreCase);
                }
                UpdatePairsSummary();
            }
        }
        ApplyFilter();
    }

    partial void OnSelectedTypeChanged(string value)
    {
        if (value == "Додано" || value == "Added")
        {
            _filterAdded = true; _filterModified = false; _filterDeleted = false; _filterRenamed = false; _filterConflict = false;
        }
        else if (value == "Видалено" || value == "Deleted")
        {
            _filterAdded = false; _filterModified = false; _filterDeleted = true; _filterRenamed = false; _filterConflict = false;
        }
        else if (value == "Змінено" || value == "Modified")
        {
            _filterAdded = false; _filterModified = true; _filterDeleted = false; _filterRenamed = false; _filterConflict = false;
        }
        else if (value == "Перейменовано" || value == "Renamed")
        {
            _filterAdded = false; _filterModified = false; _filterDeleted = false; _filterRenamed = true; _filterConflict = false;
        }
        else if (value == "Конфлікт" || value == "Conflict")
        {
            _filterAdded = false; _filterModified = false; _filterDeleted = false; _filterRenamed = false; _filterConflict = true;
        }
        else
        {
            _filterAdded = true; _filterModified = true; _filterDeleted = true; _filterRenamed = true; _filterConflict = true;
        }
        OnPropertyChanged(nameof(FilterAdded));
        OnPropertyChanged(nameof(FilterModified));
        OnPropertyChanged(nameof(FilterDeleted));
        OnPropertyChanged(nameof(FilterRenamed));
        OnPropertyChanged(nameof(FilterConflict));
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public void ApplyFilter()
    {
        FilteredItems.Clear();
        foreach (var item in Items)
        {
            bool matchPair = true;
            if (PairFilters.Count > 0)
            {
                matchPair = PairFilters.Any(pf => pf.IsSelected && pf.Name.Equals(item.PairName, StringComparison.OrdinalIgnoreCase));
            }
            else if (!string.IsNullOrEmpty(SelectedPair) &&
                     PairOptions.Count > 0 &&
                     SelectedPair != PairOptions[0] &&
                     SelectedPair != "Усі пари" &&
                     SelectedPair != "Усі завдання" &&
                     SelectedPair != "All pairs" &&
                     SelectedPair != "All tasks")
            {
                matchPair = item.PairName.Equals(SelectedPair, StringComparison.OrdinalIgnoreCase);
            }

            if (!matchPair) continue;

            bool matchType = (FilterAdded && item.IsAdded)
                          || (FilterModified && item.IsModified)
                          || (FilterDeleted && item.IsDeleted)
                          || (FilterRenamed && item.IsRenamed)
                          || (FilterConflict && item.IsConflict);
            if (!matchType) continue;

            if (!string.IsNullOrWhiteSpace(SearchText) &&
                !item.RelativePath.Contains(SearchText, StringComparison.OrdinalIgnoreCase) &&
                !item.WasPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                continue;

            FilteredItems.Add(item);
        }

        FilteredGroups.Clear();
        var roots = _pairRoots?.Invoke() ?? new Dictionary<string, (string Source, string Target)>();
        var resolvedRoots = new Dictionary<string, (string Source, string Target)>(roots, StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = _statePath?.Invoke();
            var dbDir = !string.IsNullOrEmpty(path) ? Path.GetDirectoryName(path) ?? "" : "";
            var candidates = new[]
            {
                Path.Combine(dbDir, "config.json"),
                Path.Combine(AppContext.BaseDirectory, "config.json")
            };
            foreach (var cfgPath in candidates)
            {
                if (File.Exists(cfgPath))
                {
                    var cfg = SyncConfig.Load(cfgPath);
                    foreach (var p in cfg.Pairs)
                    {
                        if (!resolvedRoots.ContainsKey(p.Name) && (!string.IsNullOrEmpty(p.Source) || !string.IsNullOrEmpty(p.Target)))
                        {
                            resolvedRoots[p.Name] = (p.Source, p.Target);
                        }
                    }
                }
            }
        }
        catch { /* best effort */ }

        var grouped = FilteredItems.GroupBy(i => i.PairName, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key);

        foreach (var grp in grouped)
        {
            resolvedRoots.TryGetValue(grp.Key, out var pairRoot);
            var groupVm = new ApprovalGroupViewModel
            {
                PairName = grp.Key,
                SourceRoot = pairRoot.Source ?? "",
                TargetRoot = pairRoot.Target ?? ""
            };

            foreach (var item in grp)
            {
                groupVm.Items.Add(item);
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ApprovalItemViewModel.IsSelected))
                    {
                        groupVm.UpdateSelectionState();
                    }
                };
            }
            groupVm.UpdateSelectionState();
            FilteredGroups.Add(groupVm);
        }

        UpdateSummary();
    }

    public void SelectAll(bool select)
    {
        foreach (var item in FilteredItems)
        {
            item.IsSelected = select;
        }
        foreach (var g in FilteredGroups)
        {
            g.UpdateSelectionState();
        }
        UpdateSummary();
        OnPropertyChanged(nameof(SelectedCount));
    }

    private void UpdateSummary()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SummaryText = I18n.T("approval.summary_total", Items.Count);
            return;
        }

        long copyBytes = 0;
        int deleteCount = 0;

        foreach (var item in selected)
        {
            if (item.IsAdded || item.IsModified)
            {
                copyBytes += item.Entry.DstSize ?? 0;
            }
            else if (item.IsDeleted)
            {
                deleteCount++;
            }
        }

        var parts = new List<string> { I18n.T("approval.summary_selected", selected.Count) };
        if (copyBytes > 0) parts.Add(I18n.T("approval.summary_copy", SyncExecutor.FormatSize(copyBytes)));
        if (deleteCount > 0) parts.Add(I18n.T("approval.summary_delete", deleteCount));

        SummaryText = string.Join(" · ", parts);
    }

    public async Task<ApprovalResult> ExecuteDecisionForItemsAsync(IEnumerable<ApprovalItemViewModel> items, DecisionKind decision, CancellationToken ct = default)
    {
        IsBusy = true;
        var totalResult = new ApprovalResult();
        var roots = _pairRoots();
        var now = DateTimeOffset.UtcNow;

        try
        {
            using var store = new StateStore(_statePath());
            var service = new ApprovalService(store, new LambdaSyncLog(msg => _log(msg)));

            foreach (var item in items.ToList())
            {
                if (!roots.TryGetValue(item.PairName, out var pairRoot))
                {
                    totalResult.Errors.Add($"[{item.PairName}] Корінь пари не налаштовано.");
                    continue;
                }

                var res = decision switch
                {
                    DecisionKind.Approve => await service.ApproveAsync(item.Id, pairRoot.Source, pairRoot.Target, now, ct),
                    DecisionKind.Dismiss => await service.DismissAsync(item.Id, pairRoot.Source, pairRoot.Target, now, ct),
                    DecisionKind.Revert => await service.RevertAsync(item.Id, pairRoot.Source, pairRoot.Target, now, ct),
                    _ => new ApprovalResult()
                };

                totalResult.ApprovedCount += res.ApprovedCount;
                totalResult.DismissedCount += res.DismissedCount;
                totalResult.RevertedCount += res.RevertedCount;
                totalResult.StaleCount += res.StaleCount;
                totalResult.ConflictCount += res.ConflictCount;
                totalResult.UnavailableCount += res.UnavailableCount;
                totalResult.Errors.AddRange(res.Errors);
            }
        }
        catch (Exception ex)
        {
            totalResult.Errors.Add($"Системна помилка виконання: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            Load();
        }

        return totalResult;
    }
}

internal sealed class LambdaSyncLog : Core.Logging.ISyncLog
{
    private readonly Action<string> _write;
    public LambdaSyncLog(Action<string> write) => _write = write;
    public void Write(Core.Logging.LogLevel level, string message) => _write($"[{level}] {message}");
}
