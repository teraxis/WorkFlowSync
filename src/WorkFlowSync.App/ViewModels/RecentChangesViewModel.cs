using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.I18n;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.App.ViewModels;

/// <summary>One file in the «recently appeared» list.</summary>
public sealed class RecentItemViewModel
{
    public required string Name { get; init; }

    /// <summary>Pair and folder, so the same file name in two places is still telling.</summary>
    public required string Where { get; init; }

    public required string When { get; init; }
    public required string SizeText { get; init; }

    /// <summary>Family of the file, which decides the badge colour.</summary>
    public required FileKind Kind { get; init; }

    /// <summary>Short mark on the badge — the extension, e.g. DOCX.</summary>
    public required string Badge { get; init; }

    /// <summary>The icon Windows shows for this file type; null when the shell had none, and the badge shows instead.</summary>
    public Avalonia.Media.Imaging.Bitmap? Icon { get; init; }

    public bool HasIcon => Icon is not null;
    public bool ShowBadge => Icon is null;

    /// <summary>Absolute path, for opening the file in Explorer. Null when the pair is no longer configured.</summary>
    public string? FullPath { get; init; }

    public bool CanOpen => FullPath is not null;
}

/// <summary>
/// The tray window's content (docs/plan-etap5.md §5.6): what has appeared in the watched folders lately.
/// Reads the state database on demand — the window is opened rarely and the query is capped, so there is
/// nothing to keep in memory between openings.
/// </summary>
public sealed partial class RecentChangesViewModel : ObservableObject
{
    /// <summary>Used when the caller supplies no limit of its own.</summary>
    public const int DefaultLimit = 40;

    private readonly Func<string> _statePath;
    private readonly Func<IReadOnlyDictionary<string, string>> _pairTargets;
    private readonly Func<int> _limit;

    /// <param name="pairTargets">Pair name → local folder, so a row can be opened in Explorer.</param>
    /// <param name="limit">How many files to list; read each time, so the setting takes effect without a restart.</param>
    public RecentChangesViewModel(Func<string> statePath, Func<IReadOnlyDictionary<string, string>> pairTargets, Func<int>? limit = null)
    {
        _statePath = statePath;
        _pairTargets = pairTargets;
        _limit = limit ?? (() => DefaultLimit);

        I18n.Instance.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(PendingBannerText));
            Load();
        };
    }

    public ObservableCollection<RecentItemViewModel> Items { get; } = new();

    [ObservableProperty] private string _summary = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    [NotifyPropertyChangedFor(nameof(PendingBannerText))]
    private int _pendingCount;

    public bool HasPending => PendingCount > 0;
    public string PendingBannerText => I18n.T("recent.banner_pending");
    public Action? OpenApprovalAction { get; set; }

    public bool IsEmpty => Items.Count == 0;

    /// <summary>Re-reads the list. Never throws: a missing or busy database just shows as «nothing yet».</summary>
    public void Load()
    {
        Items.Clear();
        try
        {
            var targets = _pairTargets();
            using var store = new StateStore(_statePath());
            PendingCount = store.Pending.CountOpen();
            var limit = Math.Clamp(_limit(), 5, 500);
            foreach (var item in store.RecentlyAdded(limit))
            {
                var folder = Path.GetDirectoryName(item.RelativePath);
                var name = Path.GetFileName(item.RelativePath);
                Items.Add(new RecentItemViewModel
                {
                    Name = name,
                    Kind = FileKinds.Of(name),
                    Badge = FileKinds.Badge(name),
                    Icon = Views.SystemFileIcons.For(name),
                    Where = string.IsNullOrEmpty(folder) ? item.Pair : $"{item.Pair} · {folder}",
                    When = Humanise(item.FirstSeenUtc),
                    SizeText = item.Size is { } size ? SyncExecutor.FormatSize(size) : "",
                    FullPath = targets.TryGetValue(item.Pair, out var root) ? Path.Combine(root, item.RelativePath) : null,
                });
            }
            Summary = Items.Count == 0
                ? I18n.T("recent.summary_empty")
                : (Items.Count == limit
                    ? I18n.T("recent.summary_count_more", Items.Count)
                    : I18n.T("recent.summary_count", Items.Count));
        }
        catch (Exception ex)
        {
            // The state database is the app's own; a failure here must not take the tray down with it.
            Summary = I18n.T("recent.read_error", ex.Message);
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>«щойно» / «14 хв тому» / «вчора, 09:12» — a time a person reads without doing arithmetic.</summary>
    public static string Humanise(DateTimeOffset utc, DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var local = utc.ToLocalTime();
        var ago = now - utc;

        var culture = I18n.Instance.Culture;
        if (ago < TimeSpan.Zero) return local.ToString("HH:mm", culture);
        if (ago < TimeSpan.FromMinutes(1)) return I18n.T("recent.just_now");
        if (ago < TimeSpan.FromHours(1)) return I18n.T("recent.minutes_ago", (int)ago.TotalMinutes);

        var today = now.ToLocalTime().Date;
        if (local.Date == today) return local.ToString("HH:mm", culture);
        if (local.Date == today.AddDays(-1)) return I18n.T("recent.yesterday", local.ToString("HH:mm", culture));
        return local.ToString("d MMMM, HH:mm", culture);
    }
}
