
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.I18n;

namespace WorkFlowSync.App.ViewModels;

/// <summary>Editable projection of <see cref="FolderPair"/> for the pair list and the pair dialog.</summary>
public sealed partial class PairViewModel : ObservableObject
{
    private readonly ICloudFilePlatform _cloudPlatform;

    public PairViewModel() : this(new WindowsCloudFilePlatform()) { }

    public PairViewModel(ICloudFilePlatform cloudPlatform)
    {
        _cloudPlatform = cloudPlatform;
        I18n.Instance.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(StateSummary));
            OnPropertyChanged(nameof(ToggleTip));
            OnPropertyChanged(nameof(IntervalSummary));
            OnPropertyChanged(nameof(Facts));
            OnPropertyChanged(nameof(SourceLabel));
            OnPropertyChanged(nameof(TargetLabel));
            OnPropertyChanged(nameof(ModeSummary));
            OnPropertyChanged(nameof(ModeHint));
            OnPropertyChanged(nameof(MaxAgeHint));
            OnPropertyChanged(nameof(AutoCleanHint));
            OnPropertyChanged(nameof(WatchHint));
            OnPropertyChanged(nameof(CloudFilesHint));
            OnPropertyChanged(nameof(FreeUpSpaceHint));
            OnPropertyChanged(nameof(DiskQuotaHint));
            OnPropertyChanged(nameof(RotationHint));
            OnPropertyChanged(nameof(LinksSummary));
            OnPropertyChanged(nameof(SourceKindSummary));
            OnPropertyChanged(nameof(TargetKindSummary));
            OnPropertyChanged(nameof(FolderName));
            OnPropertyChanged(nameof(HasFolderName));
            OnPropertyChanged(nameof(StatusBadgeText));
            OnPropertyChanged(nameof(LastSyncSummary));
            OnPropertyChanged(nameof(SyncStatusTitle));
            OnPropertyChanged(nameof(SyncStatusSubtext));
            OnPropertyChanged(nameof(ModeSummaryShort));
            OnPropertyChanged(nameof(ApprovalSummaryShort));
            OnPropertyChanged(nameof(VersioningSummaryShort));
            OnPropertyChanged(nameof(WatchSummaryShort));
            OnPropertyChanged(nameof(ExcludeCountSummary));
        };
    }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _target = "";
    [ObservableProperty] private LinkMode _links = LinkMode.Follow;

    /// <summary>Whether the pair reacts to changes as they happen or only on the interval.</summary>
    [ObservableProperty] private WatchMode _watch = WatchMode.Auto;

    /// <summary>What to do with files that live only in the cloud when they have to be copied out.</summary>
    [ObservableProperty] private CloudFileMode _cloudFiles = CloudFileMode.Skip;

    /// <summary>Ask Windows/OneDrive to make each target copy online-only as soon as it is uploaded.</summary>
    [ObservableProperty] private bool _freeUpSpaceAfterCopy;

    /// <summary>Protect a free-space reserve independently of the destination's storage provider.</summary>
    [ObservableProperty] private bool _diskQuotaEnabled;

    /// <summary>Disk reserve protected before every target write.</summary>
    [ObservableProperty] private int _minFreeSpaceGb = 5;

    /// <summary>Permanently delete oldest verified copies on any target when the quota is enabled.</summary>
    [ObservableProperty] private bool _rotateOnLowSpace;

    /// <summary>Mirror with memory (default) or full two-way synchronisation.</summary>
    [ObservableProperty] private SyncMode _mode = SyncMode.Mirror;

    /// <summary>
    /// How often this pair is checked. Every pair carries its own now — there is no shared schedule to
    /// fall back to, because one number never fitted both a working folder and a 50 000-folder archive.
    /// </summary>
    [ObservableProperty] private int _intervalMinutes = 30;

    public string IntervalSummary => IntervalMinutes % 60 == 0 && IntervalMinutes >= 60
        ? I18n.T("pair.interval_hours_format", IntervalMinutes / 60)
        : I18n.T("pair.interval_mins_format", IntervalMinutes);

    /// <summary>
    /// Intake filter. False = take whatever the source has; true = copy only what appeared within
    /// <see cref="MaxAgeDays"/>. Older files stay in the source and are simply never brought over.
    /// </summary>
    [ObservableProperty] private bool _hasMaxAge;
    [ObservableProperty] private int _maxAgeDays = 365;

    /// <summary>
    /// Auto-clean. False = our copies stay forever; true = a copy we added more than
    /// <see cref="AutoCleanDays"/> ago goes to the Recycle Bin. Mirror mode only.
    /// </summary>
    [ObservableProperty] private bool _hasAutoClean;
    [ObservableProperty] private int _autoCleanDays = 365;

    /// <summary>One exclude pattern per line.</summary>
    [ObservableProperty] private string _excludeText = "";

    /// <summary>Approval mode: target B changes require human approval (two-way only).</summary>
    [ObservableProperty] private bool _requireApproval;

    /// <summary>Versioning mode: save snapshots in .wfsversions before revert/deletion.</summary>
    [ObservableProperty] private bool _versioning;
    [ObservableProperty] private int _versionsKeepDays = 30;
    [ObservableProperty] private int _versionsMaxGb = 5;

    [ObservableProperty] private DateTimeOffset? _lastSyncTime;
    [ObservableProperty] private int _lastSyncFilesCount;
    [ObservableProperty] private int _lastSyncErrors;

    public string FolderName
    {
        get
        {
            var folder = Source.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            return !string.IsNullOrWhiteSpace(folder) && !string.Equals(folder, Name, StringComparison.OrdinalIgnoreCase)
                ? folder
                : "";
        }
    }

    public bool HasFolderName => !string.IsNullOrEmpty(FolderName);

    public bool IsPaused => !Enabled;
    public bool IsActiveAndIdle => Enabled && !IsBusy;
    public bool IsUpToDate => Enabled && !IsBusy && LastSyncErrors == 0;
    public bool HasError => Enabled && !IsBusy && LastSyncErrors > 0;

    public string StatusBadgeText => IsBusy
        ? I18n.T("pair.badge_syncing")
        : Enabled
            ? I18n.T("pair.badge_active")
            : I18n.T("pair.badge_paused");

    public string LastSyncSummary => MainViewModel.FormatRelative(LastSyncTime);

    public string SyncStatusTitle
    {
        get
        {
            if (IsBusy) return I18n.T("tasks.status_syncing");
            if (!Enabled) return I18n.T("tasks.status_paused");
            if (LastSyncErrors > 0) return I18n.T("tasks.status_error");
            return I18n.T("tasks.status_up_to_date");
        }
    }

    public string SyncStatusSubtext
    {
        get
        {
            if (IsBusy)
            {
                return LastSyncFilesCount > 0
                    ? I18n.T("tasks.status_processed_files", LastSyncFilesCount.ToString("N0", I18n.Instance.Culture))
                    : I18n.T("tasks.status_processing");
            }
            if (!Enabled) return I18n.T("tasks.status_auto_disabled");
            if (LastSyncTime is null) return I18n.T("tasks.status_never_synced");
            return I18n.T("tasks.last_sync_prefix", LastSyncSummary);
        }
    }

    public string ModeSummaryShort => IsMirror
        ? I18n.T("pair.mode_mirror_short")
        : I18n.T("pair.mode_twoway_short");

    public string ApprovalSummaryShort => RequireApproval
        ? I18n.T("pair.approval_with")
        : I18n.T("pair.approval_without");

    public string VersioningSummaryShort => Versioning
        ? $"{VersionsKeepDays} d ({VersionsMaxGb} GB)"
        : I18n.T("pair.facts_off");

    public string WatchSummaryShort => Watch switch
    {
        WatchMode.On => I18n.T("pair.watch_on"),
        WatchMode.Off => I18n.T("pair.watch_off"),
        _ => I18n.T("pair.facts_auto"),
    };

    public bool ShowWatch => Watch == WatchMode.On;
    public bool ShowSchedule => Watch == WatchMode.Off;
    public bool HasExcludes => ExcludeCount > 0;
    public bool HasMoreItemsAfterLinks => RequireApproval || ShowWatch || HasExcludes;
    public bool HasMoreItemsAfterApproval => ShowWatch || HasExcludes;
    public bool HasMoreItemsAfterWatch => HasExcludes;
    public string ExcludeCountSummary => I18n.T("pair.excludes_count", ExcludeCount);

    public void RaiseSyncState()
    {
        OnPropertyChanged(nameof(LastSyncSummary));
        OnPropertyChanged(nameof(StatusBadgeText));
        OnPropertyChanged(nameof(SyncStatusTitle));
        OnPropertyChanged(nameof(SyncStatusSubtext));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsActiveAndIdle));
        OnPropertyChanged(nameof(IsUpToDate));
        OnPropertyChanged(nameof(HasError));
    }

    public static IReadOnlyList<LinkMode> LinkModes { get; } = Enum.GetValues<LinkMode>();
    public static IReadOnlyList<SyncMode> Modes { get; } = Enum.GetValues<SyncMode>();
    public static IReadOnlyList<WatchMode> WatchModes { get; } = Enum.GetValues<WatchMode>();
    public static IReadOnlyList<CloudFileMode> CloudFileModes { get; } = Enum.GetValues<CloudFileMode>();

    /// <summary>
    /// Whether this pair can ever meet a cloud placeholder. They exist only on a local disk, and only the
    /// side the pass READS matters: a mirror reads its source and never its target, while two equal sides
    /// are both read. Gating on "two-way only" was too crude — it disabled the setting for a mirror whose
    /// source is a OneDrive folder, where it is exactly what is needed.
    /// </summary>
    public bool CloudFilesApplies => IsMirror
        ? Core.CloudFiles.Possible(Source)
        : Core.CloudFiles.Possible(Source) || Core.CloudFiles.Possible(Target);

    /// <summary>Explains the cloud-files choice, why it is greyed out, and what «Завантажувати» really costs.</summary>
    public string CloudFilesHint
    {
        get
        {
            if (!CloudFilesApplies)
                return IsMirror
                    ? I18n.T("pair.cloud_not_applicable_mirror")
                    : I18n.T("pair.cloud_not_applicable_twoway");

            return "";
        }
    }

    /// <summary>
    /// Unlike the broad cloud-placeholder setting above, this write-side feature is available only when
    /// Windows confirms that the destination belongs to a registered Files On-Demand sync root.
    /// </summary>
    public bool FreeUpSpaceApplies => Core.CloudFiles.Possible(Target) && _cloudPlatform.IsSyncRoot(Target);

    public string FreeUpSpaceHint => FreeUpSpaceApplies
        ? I18n.T("pair.cloud_offload_hint")
        : I18n.T("pair.cloud_offload_not_applicable");

    public bool DiskQuotaApplies => !string.IsNullOrWhiteSpace(Target);

    public string DiskQuotaHint => DiskQuotaApplies
        ? I18n.T("pair.disk_quota_hint")
        : I18n.T("pair.disk_quota_not_applicable");

    public bool RotationApplies => DiskQuotaEnabled && DiskQuotaApplies;

    public string RotationHint => RotationApplies
        ? I18n.T("pair.rotation_hint")
        : I18n.T("pair.rotation_not_applicable");

    /// <summary>What kind of storage the first folder sits on, as far as it can be told without touching the network.</summary>
    public string SourceKindSummary => KindSummary(Source);
    public string TargetKindSummary => KindSummary(Target);

    public Geometry? SourceIcon => KindIcon(Source);
    public Geometry? TargetIcon => KindIcon(Target);

    /// <summary>Why the pair reacts as fast (or as slowly) as it does — the two facts together explain it.</summary>
    public string WatchHint => "";

    private static Geometry? KindIcon(string path) => string.IsNullOrWhiteSpace(path)
        ? Icon("IconHardDrive")
        : RootProbe.QuickKind(path) switch
        {
            RootKind.Removable => Icon("IconUsb"),
            RootKind.LanShare or RootKind.RemoteShare => Icon("IconServer"),
            _ => Icon("IconHardDrive"),
        };

    private static string KindSummary(string path) => string.IsNullOrWhiteSpace(path)
        ? "—"
        : RootProbe.QuickKind(path) switch
        {
            RootKind.Local => I18n.T("pair.storage_local"),
            RootKind.Removable => I18n.T("pair.storage_removable"),
            _ => I18n.T("pair.storage_network"),
        };

    public bool IsMirror => Mode == SyncMode.Mirror;

    /// <summary>Arrow between the two paths in the pair card: single for mirror, double for two-way.</summary>
    public Geometry? ModeIcon => Icon(IsMirror ? "IconArrowRight" : "IconArrowsBoth");

    public string ModeSummary => IsMirror
        ? I18n.T("pair.mode_mirror_hint")
        : "";

    public string SourceLabel => IsMirror ? I18n.T("pair.source_label_mirror") : I18n.T("pair.source_label_twoway");
    public string TargetLabel => IsMirror ? I18n.T("pair.target_label_mirror") : I18n.T("pair.target_label_twoway");

    public string ModeHint => ModeSummary;

    public string MaxAgeHint => IsMirror
        ? I18n.T("pair.transfer_hint")
        : I18n.T("pair.transfer_hint");

    public string AutoCleanHint => IsMirror
        ? I18n.T("pair.autoclean_hint")
        : I18n.T("pair.autoclean_hint");

    public string MaxAgeSummary => HasMaxAge ? I18n.T("pair.facts_maxage", MaxAgeDays) : I18n.T("pair.facts_any_age");

    public string AutoCleanSummary => HasAutoClean ? I18n.T("pair.facts_autoclean", AutoCleanDays) : I18n.T("pair.facts_off");

    /// <summary>True while this very pair is being synchronised (drives the row's buttons).</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Play/pause icon of the single state button (geometry from Styles/Icons.axaml).</summary>
    public Geometry? ToggleIcon => Icon(Enabled ? "IconPauseFill" : "IconPlayFill");
    public string ToggleGlyph => Enabled ? "⏸" : "▶";
    public string ToggleTip => Enabled ? I18n.T("pair.toggle_tip_pause") : I18n.T("pair.toggle_tip_resume");
    public string StateSummary => IsBusy ? I18n.T("pair.state_checking") : Enabled ? I18n.T("pair.state_running") : I18n.T("pair.state_paused");

    /// <summary>One line of secondary facts under the paths.</summary>
    public string Facts
    {
        get
        {
            var parts = new List<string> { IsMirror ? I18n.T("pair.facts_mirror") : I18n.T("pair.facts_twoway"), I18n.T("pair.facts_first", SourceKindSummary) };
            parts.Add(IntervalSummary);
            parts.Add(LinksSummary);
            if (IsMirror)
            {
                if (HasMaxAge) parts.Add(MaxAgeSummary);
                if (HasAutoClean) parts.Add(AutoCleanSummary);
            }
            else
            {
                if (RequireApproval) parts.Add(I18n.T("pair.facts_approval"));
            }
            if (Versioning) parts.Add(I18n.T("pair.facts_versioning", VersionsKeepDays, VersionsMaxGb));
            if (DiskQuotaEnabled) parts.Add(I18n.T("pair.facts_disk_quota", MinFreeSpaceGb));
            if (FreeUpSpaceAfterCopy) parts.Add(I18n.T("pair.facts_cloud_offload"));
            if (RotateOnLowSpace) parts.Add(I18n.T("pair.facts_rotation"));
            if (ExcludeCount > 0) parts.Add(I18n.T("pair.facts_excludes", ExcludeCount));
            // Auto is the default and needs no explanation; the other two are choices worth showing.
            if (Watch != WatchMode.Auto) parts.Add(Watch == WatchMode.On ? I18n.T("pair.facts_watch_on") : I18n.T("pair.facts_watch_off"));
            return string.Join("   ·   ", parts);
        }
    }

    private static Geometry? Icon(string key)
    {
        var app = Avalonia.Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value as Geometry : null;
    }

    public string LinksSummary => Links switch
    {
        LinkMode.Follow => I18n.T("pair.links_follow"),
        LinkMode.Skip => I18n.T("pair.links_skip"),
        LinkMode.Recreate => I18n.T("pair.links_recreate"),
        _ => Links.ToString(),
    };


    public int ExcludeCount => ParseExcludes(ExcludeText).Count;

    public static PairViewModel FromModel(FolderPair p) => new()
    {
        Name = p.Name,
        Enabled = p.Enabled,
        Source = p.Source,
        Target = p.Target,
        Mode = p.Mode,
        Links = p.Links,
        Watch = p.Watch,
        CloudFiles = p.CloudFiles,
        FreeUpSpaceAfterCopy = p.FreeUpSpaceAfterCopy,
        DiskQuotaEnabled = p.DiskQuotaEnabled,
        MinFreeSpaceGb = p.MinFreeSpaceGb,
        RotateOnLowSpace = p.RotateOnLowSpace,
        IntervalMinutes = p.Interval is { } iv ? Math.Max(1, (int)Math.Round(iv.TotalMinutes)) : 30,
        HasMaxAge = p.MaxAge is not null,
        MaxAgeDays = p.MaxAge is { } ma ? Math.Max(1, (int)Math.Round(ma.TotalDays)) : 365,
        HasAutoClean = p.AutoClean is not null,
        AutoCleanDays = p.AutoClean is { } ac ? Math.Max(1, (int)Math.Round(ac.TotalDays)) : 365,
        ExcludeText = string.Join(Environment.NewLine, p.Exclude),
        RequireApproval = p.RequireApproval,
        Versioning = p.Versioning,
        VersionsKeepDays = p.VersionsKeepDays < 1 ? 30 : p.VersionsKeepDays,
        VersionsMaxGb = p.VersionsMaxGb < 1 ? 5 : p.VersionsMaxGb,
    };

    public FolderPair ToModel() => new()
    {
        Name = Name.Trim(),
        Enabled = Enabled,
        Source = Source.Trim(),
        Target = Target.Trim(),
        Mode = Mode,
        Links = Links,
        Watch = Watch,
        CloudFiles = CloudFiles,
        FreeUpSpaceAfterCopy = FreeUpSpaceAfterCopy,
        DiskQuotaEnabled = DiskQuotaEnabled,
        MinFreeSpaceGb = Math.Clamp(MinFreeSpaceGb, 1, 1000),
        RotateOnLowSpace = DiskQuotaEnabled && RotateOnLowSpace,
        Interval = TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes)),
        MaxAge = HasMaxAge ? TimeSpan.FromDays(MaxAgeDays) : null,
        // Auto-clean is a mirror-only idea; a two-way pair carries real deletions across instead.
        AutoClean = HasAutoClean && Mode == SyncMode.Mirror ? TimeSpan.FromDays(AutoCleanDays) : null,
        Exclude = ParseExcludes(ExcludeText),
        RequireApproval = Mode == SyncMode.TwoWay && RequireApproval,
        Versioning = Versioning,
        VersionsKeepDays = Math.Clamp(VersionsKeepDays, 1, 3650),
        VersionsMaxGb = Math.Clamp(VersionsMaxGb, 1, 1000),
    };

    public PairViewModel Clone() => FromModel(ToModel());

    public void CopyFrom(PairViewModel other)
    {
        Name = other.Name;
        Enabled = other.Enabled;
        Source = other.Source;
        Target = other.Target;
        Mode = other.Mode;
        Links = other.Links;
        Watch = other.Watch;
        CloudFiles = other.CloudFiles;
        FreeUpSpaceAfterCopy = other.FreeUpSpaceAfterCopy;
        DiskQuotaEnabled = other.DiskQuotaEnabled;
        MinFreeSpaceGb = other.MinFreeSpaceGb;
        RotateOnLowSpace = other.RotateOnLowSpace;
        IntervalMinutes = other.IntervalMinutes;
        HasMaxAge = other.HasMaxAge;
        MaxAgeDays = other.MaxAgeDays;
        HasAutoClean = other.HasAutoClean;
        AutoCleanDays = other.AutoCleanDays;
        ExcludeText = other.ExcludeText;
        RequireApproval = other.RequireApproval;
        Versioning = other.Versioning;
        VersionsKeepDays = other.VersionsKeepDays;
        VersionsMaxGb = other.VersionsMaxGb;
        RaiseSummaries();
    }

    public void RaiseSummaries()
    {
        OnPropertyChanged(nameof(Facts));
        OnPropertyChanged(nameof(ToggleIcon));
        OnPropertyChanged(nameof(ToggleGlyph));
        OnPropertyChanged(nameof(ToggleTip));
        OnPropertyChanged(nameof(StateSummary));
        OnPropertyChanged(nameof(MaxAgeSummary));
        OnPropertyChanged(nameof(AutoCleanSummary));
        OnPropertyChanged(nameof(LinksSummary));
        OnPropertyChanged(nameof(ExcludeCount));
        OnPropertyChanged(nameof(SourceKindSummary));
        OnPropertyChanged(nameof(TargetKindSummary));
        OnPropertyChanged(nameof(WatchHint));
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(HasFolderName));
        OnPropertyChanged(nameof(ModeSummaryShort));
        OnPropertyChanged(nameof(ApprovalSummaryShort));
        OnPropertyChanged(nameof(VersioningSummaryShort));
        OnPropertyChanged(nameof(WatchSummaryShort));
        OnPropertyChanged(nameof(ShowWatch));
        OnPropertyChanged(nameof(HasExcludes));
        OnPropertyChanged(nameof(ExcludeCountSummary));
        RaiseSyncState();
        RaiseMode();
    }

    private static List<string> ParseExcludes(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(HasFolderName));
    }

    partial void OnEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleIcon));
        OnPropertyChanged(nameof(ToggleGlyph));
        OnPropertyChanged(nameof(ToggleTip));
        OnPropertyChanged(nameof(StateSummary));
        RaiseSyncState();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(StateSummary));
        RaiseSyncState();
    }

    partial void OnIntervalMinutesChanged(int value) => RaiseFacts(nameof(IntervalSummary));

    partial void OnHasMaxAgeChanged(bool value) => RaiseFacts(nameof(MaxAgeSummary));
    partial void OnMaxAgeDaysChanged(int value) => RaiseFacts(nameof(MaxAgeSummary));
    partial void OnHasAutoCleanChanged(bool value) => RaiseFacts(nameof(AutoCleanSummary));
    partial void OnAutoCleanDaysChanged(int value) => RaiseFacts(nameof(AutoCleanSummary));
    partial void OnLinksChanged(LinkMode value) => RaiseFacts(nameof(LinksSummary));

    partial void OnWatchChanged(WatchMode value)
    {
        RaiseFacts(nameof(WatchHint));
        OnPropertyChanged(nameof(WatchSummaryShort));
        OnPropertyChanged(nameof(ShowWatch));
        OnPropertyChanged(nameof(ShowSchedule));
        OnPropertyChanged(nameof(HasMoreItemsAfterLinks));
        OnPropertyChanged(nameof(HasMoreItemsAfterApproval));
        OnPropertyChanged(nameof(HasMoreItemsAfterWatch));
        OnPropertyChanged(nameof(FreeUpSpaceApplies));
        OnPropertyChanged(nameof(FreeUpSpaceHint));
    }

    partial void OnRequireApprovalChanged(bool value)
    {
        RaiseFacts(nameof(Facts));
        OnPropertyChanged(nameof(ApprovalSummaryShort));
        OnPropertyChanged(nameof(HasMoreItemsAfterLinks));
        OnPropertyChanged(nameof(HasMoreItemsAfterApproval));
    }

    partial void OnVersioningChanged(bool value)
    {
        RaiseFacts(nameof(Facts));
        OnPropertyChanged(nameof(VersioningSummaryShort));
    }

    partial void OnFreeUpSpaceAfterCopyChanged(bool value) => RaiseFacts(nameof(FreeUpSpaceHint));
    partial void OnDiskQuotaEnabledChanged(bool value)
    {
        if (!value && RotateOnLowSpace) RotateOnLowSpace = false;
        RaiseFacts(nameof(DiskQuotaHint));
        OnPropertyChanged(nameof(RotationApplies));
        OnPropertyChanged(nameof(RotationHint));
    }
    partial void OnMinFreeSpaceGbChanged(int value) => RaiseFacts(nameof(DiskQuotaHint));
    partial void OnRotateOnLowSpaceChanged(bool value) => RaiseFacts(nameof(RotationHint));

    partial void OnCloudFilesChanged(CloudFileMode value) => RaiseCloudFiles();

    // The storage type is derived from the path, so it has to be re-read whenever the path is edited —
    // and with it whether cloud placeholders are even possible for this pair.
    partial void OnSourceChanged(string value)
    {
        RaiseFacts(nameof(SourceKindSummary));
        RaiseCloudFiles();
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(HasFolderName));
    }

    partial void OnTargetChanged(string value)
    {
        RaiseFacts(nameof(TargetKindSummary));
        RaiseCloudFiles();
        OnPropertyChanged(nameof(FreeUpSpaceApplies));
        OnPropertyChanged(nameof(FreeUpSpaceHint));
        OnPropertyChanged(nameof(DiskQuotaApplies));
        OnPropertyChanged(nameof(DiskQuotaHint));
        OnPropertyChanged(nameof(RotationApplies));
        OnPropertyChanged(nameof(RotationHint));
    }

    private void RaiseCloudFiles()
    {
        OnPropertyChanged(nameof(CloudFilesApplies));
        OnPropertyChanged(nameof(CloudFilesHint));
    }

    partial void OnModeChanged(SyncMode value)
    {
        RaiseMode();
        RaiseFacts(nameof(MaxAgeSummary));
        OnPropertyChanged(nameof(AutoCleanSummary));
    }

    private void RaiseMode()
    {
        OnPropertyChanged(nameof(IsMirror));
        OnPropertyChanged(nameof(ModeIcon));
        OnPropertyChanged(nameof(ModeSummary));
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(MaxAgeHint));
        OnPropertyChanged(nameof(AutoCleanHint));
        OnPropertyChanged(nameof(RotationApplies));
        OnPropertyChanged(nameof(RotationHint));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(TargetLabel));
        RaiseCloudFiles();          // a mirror reads its source, two-way reads both — the answer changes
    }
    partial void OnExcludeTextChanged(string value) => RaiseFacts(nameof(ExcludeCount));

    private void RaiseFacts(string name)
    {
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(Facts));
    }
}
