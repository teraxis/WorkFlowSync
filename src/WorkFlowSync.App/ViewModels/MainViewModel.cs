using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkFlowSync.App.Services;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.I18n;

namespace WorkFlowSync.App.ViewModels;

/// <summary>
/// Edits config.json: folder pairs + settings. No Avalonia types here (dialogs live in the window code-behind)
/// so the mapping to <see cref="SyncConfig"/> stays unit-testable.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public string ConfigPath { get; }
    public string Title => $"{WorkFlowSyncInfo.Name} {WorkFlowSyncInfo.Version}";
    public string Version => I18n.T("app.version_format", WorkFlowSyncInfo.Version);

    public ObservableCollection<PairViewModel> Pairs { get; } = new();

    [ObservableProperty] private PairViewModel? _selectedPair;

    /// <summary>Which page the navigation rail shows: 0 folders, 1 activity, 2 settings.</summary>
    [ObservableProperty] private int _selectedPage;

    public bool IsFoldersPage => SelectedPage == 0;
    public bool IsActivityPage => SelectedPage == 1;
    public bool IsSettingsPage => SelectedPage == 2;

    public string PageTitle => SelectedPage switch
    {
        0 => I18n.T("nav.tasks"),
        1 => I18n.T("nav.events"),
        _ => I18n.T("nav.settings"),
    };

    public IReadOnlyList<CpuLoad> CpuLoads { get; } = new[] { CpuLoad.Balanced, CpuLoad.Full, CpuLoad.Low };

    public IReadOnlyList<AppTheme> Themes { get; } = new[] { AppTheme.System, AppTheme.Light, AppTheme.Dark };

    public IReadOnlyList<string> Languages { get; } = new[] { "uk", "en", "system" };

    /// <summary>Applied immediately and remembered in the config.</summary>
    [ObservableProperty] private AppTheme _theme = AppTheme.System;

    [ObservableProperty] private string _language = "uk";

    // Settings tab
    [ObservableProperty] private int _intervalMinutes = 30;
    [ObservableProperty] private string _statePath = "state.db";
    [ObservableProperty] private string _logPath = "logs";
    [ObservableProperty] private int _scanParallelism = 8;

    /// <summary>How much of the machine a pass may take (docs F5).</summary>
    [ObservableProperty] private CpuLoad _cpuLoad = CpuLoad.Balanced;
    [ObservableProperty] private int _scanBufferKb = 256;
    [ObservableProperty] private int _logKeepDays = 30;

    /// <summary>Whether a finished pass may say something (docs/product/features/tray.md).</summary>
    [ObservableProperty] private NotifyMode _notifications = NotifyMode.All;

    /// <summary>How many files the «Останні зміни» window lists.</summary>
    [ObservableProperty] private int _recentLimit = 40;

    /// <summary>Hours when routine messages are held back. Equal values = no quiet period.</summary>
    [ObservableProperty] private int _quietHoursFrom;
    [ObservableProperty] private int _quietHoursTo;

    public static IReadOnlyList<NotifyMode> NotifyModes { get; } = Enum.GetValues<NotifyMode>();

    /// <summary>Spells out what the current choice means, quiet hours included.</summary>
    public string NotificationsHint => Notifications switch
    {
        NotifyMode.Off => I18n.T("hint.notify.off"),
        NotifyMode.ErrorsOnly => I18n.T("hint.notify.errors") + QuietSuffix,
        _ => I18n.T("hint.notify.all") + QuietSuffix,
    };

    private string QuietSuffix => QuietHoursFrom == QuietHoursTo
        ? ""
        : I18n.T("hint.notify.quiet", QuietHoursFrom, QuietHoursTo);

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _statusIsError;

    public bool HasPairs => Pairs.Count > 0;
    public bool HasSelection => SelectedPair is not null;

    public RunViewModel Run { get; }
    public AutostartViewModel Autostart { get; }

    /// <summary>The periodic checker. Shared by the «Стан» tab switch and the tray menu.</summary>
    public BackgroundLoop Background { get; }

    public MainViewModel(string configPath)
    {
        ConfigPath = configPath;
        Pairs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPairs));
        I18n.Instance.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(Version));
            OnPropertyChanged(nameof(NotificationsHint));
            OnPropertyChanged(nameof(CpuLoadHint));
            OnPropertyChanged(nameof(Languages));
            OnPropertyChanged(nameof(Themes));
            OnPropertyChanged(nameof(CpuLoads));
            OnPropertyChanged(nameof(NotifyModes));
        };
        Background = new BackgroundLoop(ConfigPath, ResolveLogDir, () => LogKeepDays);
        Run = new RunViewModel(ToConfig, () => ConfigPath, Background, () => false);
        Autostart = new AutostartViewModel(() => ConfigPath, () => IntervalMinutes);
        Load();
        Run.RefreshSummary();
    }

    /// <summary>Folder that holds config.json, state.db and logs — next to the executable by default.</summary>
    public string AppFolder => Path.GetDirectoryName(Path.GetFullPath(ConfigPath))!;

    public string StateFullPath => Path.IsPathRooted(StatePath.Trim()) ? StatePath.Trim() : Path.Combine(AppFolder, StatePath.Trim().Length == 0 ? "state.db" : StatePath.Trim());
    public string LogFullPath => ResolveLogDir();

    private void RaiseFolderPaths()
    {
        OnPropertyChanged(nameof(AppFolder));
        OnPropertyChanged(nameof(StateFullPath));
        OnPropertyChanged(nameof(LogFullPath));
    }

    public string ResolveLogDir()
    {
        var log = LogPath.Trim();
        if (log.Length == 0) log = "logs";
        return Path.IsPathRooted(log) ? log : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ConfigPath))!, log);
    }

    public void Load()
    {
        try
        {
            var created = false;
            if (!File.Exists(ConfigPath))
            {
                // Portable layout: the config lives next to the executable from the very first start.
                ConfigFile.Save(new SyncConfig(), ConfigPath);
                created = true;
            }
            var cfg = ConfigFile.LoadOrDefault(ConfigPath);
            Apply(cfg);
            // No "loaded X" chatter: the status line is for things the user has to notice.
            SetStatus(created ? $"Створено config.json у теці програми: {AppFolder}" : "", error: false);
            RaiseFolderPaths();
        }
        catch (Exception ex)
        {
            SetStatus($"Не вдалося прочитати конфіг: {ex.Message}", error: true);
        }
    }

    private bool _loading;

    private void Apply(SyncConfig cfg)
    {
        _loading = true;
        Pairs.Clear();
        foreach (var p in cfg.Pairs)
        {
            var vm = PairViewModel.FromModel(p);
            // A config written before schedules moved onto the pairs has none of its own: carry the old
            // shared value over, so nothing silently starts checking at a different rate.
            if (p.Interval is null) vm.IntervalMinutes = Math.Max(1, (int)Math.Round(cfg.Interval.TotalMinutes));
            vm.PropertyChanged += (_, _) => MarkDirty();
            Pairs.Add(vm);
        }
        IntervalMinutes = Math.Max(1, (int)Math.Round(cfg.Interval.TotalMinutes));
        StatePath = cfg.StatePath;
        LogPath = cfg.LogPath;
        ScanParallelism = cfg.ScanParallelism;
        CpuLoad = cfg.CpuLoad;
        ScanBufferKb = Math.Max(4, cfg.ScanBufferSize / 1024);
        LogKeepDays = cfg.LogKeepDays;
        Theme = cfg.Theme;
        Language = string.IsNullOrWhiteSpace(cfg.Language) ? "uk" : cfg.Language;
        I18n.Instance.SetLanguage(Language);
        Notifications = cfg.Notifications;
        RecentLimit = cfg.RecentLimit;
        QuietHoursFrom = cfg.QuietHoursFrom;
        QuietHoursTo = cfg.QuietHoursTo;
        _loading = false;
        FollowPairStates();
    }

    /// <summary>
    /// The background checker simply follows the pairs: it runs while at least one pair is in the «виконується»
    /// state and stops once they are all paused. There is no separate switch.
    /// </summary>
    public void FollowPairStates()
    {
        var active = Pairs.Count(p => p.Enabled);
        if (active > 0 && !Background.IsActive && Background.State != Services.LoopState.Paused) Background.Start();
        else if (active == 0 && Background.IsActive) Background.Stop();
        Run?.RefreshBackgroundStatus(active);
    }

    public SyncConfig ToConfig() => new()
    {
        Pairs = Pairs.Select(p => p.ToModel()).ToList(),
        // Kept as the fallback for hand-edited configs and as the Task Scheduler period: the shortest
        // pair interval is the only value that makes sense for a single scheduled task.
        Interval = TimeSpan.FromMinutes(Pairs.Count > 0 ? Pairs.Min(p => p.IntervalMinutes) : IntervalMinutes),
        StatePath = StatePath.Trim(),
        LogPath = LogPath.Trim(),
        ScanParallelism = ScanParallelism,
        CpuLoad = CpuLoad,
        ScanBufferSize = ScanBufferKb * 1024,
        LogKeepDays = LogKeepDays,
        Theme = Theme,
        Language = Language,
        Notifications = Notifications,
        RecentLimit = RecentLimit,
        QuietHoursFrom = QuietHoursFrom,
        QuietHoursTo = QuietHoursTo,
    };

    /// <summary>Returns validation problems (empty = OK).</summary>
    public IReadOnlyList<string> Validate() => ToConfig().Validate();

    /// <summary>
    /// Every change is written to config.json at once (atomic tmp + rename): there is no «Зберегти» button.
    /// Invalid input (only possible via the pair dialog, which validates itself) is reported and not written.
    /// </summary>
    public bool SaveNow()
    {
        if (_loading) return false;
        var problems = Validate();
        if (problems.Count > 0)
        {
            SetStatus(string.Join("  •  ", problems), error: true);
            return false;
        }
        try
        {
            ConfigFile.Save(ToConfig(), ConfigPath);
            if (StatusIsError) SetStatus("", error: false);
            FollowPairStates();
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"Не вдалося зберегти налаштування: {ex.Message}", error: true);
            return false;
        }
    }

    /// <summary>Adds a pair prepared by the dialog.</summary>
    public void AddPair(PairViewModel pair)
    {
        pair.RaiseSummaries();
        pair.PropertyChanged += (_, _) => MarkDirty();
        Pairs.Add(pair);
        SelectedPair = pair;
        SaveNow();
    }

    public void ReplacePair(PairViewModel target, PairViewModel edited)
    {
        target.CopyFrom(edited);
        SaveNow();
    }

    /// <summary>Removes a pair from the list (its state rows stay, so re-adding it keeps the memory).</summary>
    public void RemovePair(PairViewModel pair)
    {
        Pairs.Remove(pair);
        if (ReferenceEquals(SelectedPair, pair)) SelectedPair = null;
        SaveNow();
        SetStatus($"Завдання «{pair.Name}» видалено зі списку.", error: false);
    }

    public string SuggestPairName(string source)
    {
        // Last path segment; Path.GetFileName returns "" for a share root like \\srv\share, so split by hand.
        var baseName = source.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        baseName = baseName.TrimEnd(':');
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "task";
        var name = baseName;
        var i = 2;
        while (Pairs.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName}-{i++}";
        return name;
    }

    /// <summary>
    /// The row's play/pause button. Applies at once: the new state goes into config.json (the checker reads it from
    /// disk), the checker starts or stops accordingly, and a freshly started pair is checked immediately.
    /// </summary>
    public async Task TogglePairAsync(PairViewModel pair)
    {
        pair.Enabled = !pair.Enabled;
        if (!SaveNow()) return;

        if (pair.Enabled)
        {
            SetStatus($"«{pair.Name}» — активне: перевіряю зараз, далі кожні {IntervalMinutes} хв.", error: false);
            await RunPairAsync(pair);
        }
        else
        {
            SetStatus($"«{pair.Name}» — на паузі: автоматичні перевірки цього завдання зупинено.", error: false);
        }
    }

    /// <summary>Runs one pair right now (works for paused pairs too).</summary>
    public async Task RunPairAsync(PairViewModel pair)
    {
        if (Run.IsRunning) { SetStatus("Прохід уже виконується.", error: true); return; }
        pair.IsBusy = true;
        try
        {
            await Run.RunPairAsync(pair.Name);
            SetStatus(Run.LastResult, error: false);
        }
        finally
        {
            pair.IsBusy = false;
        }
    }

    /// <summary>Any edited setting lands in config.json immediately.</summary>
    private void MarkDirty() => SaveNow();

    private void SetStatus(string text, bool error)
    {
        StatusText = text;
        StatusIsError = error;
    }

    partial void OnSelectedPageChanged(int value)
    {
        OnPropertyChanged(nameof(IsFoldersPage));
        OnPropertyChanged(nameof(IsActivityPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(PageTitle));
        if (IsActivityPage) Run.RefreshSummary();
    }

    partial void OnThemeChanged(AppTheme value)
    {
        ThemeApplied?.Invoke(value);
        MarkDirty();
    }

    /// <summary>Raised when the theme must be applied to the running application (wired in App.axaml.cs).</summary>
    public event Action<AppTheme>? ThemeApplied;

    partial void OnSelectedPairChanged(PairViewModel? value) => OnPropertyChanged(nameof(HasSelection));
    partial void OnIntervalMinutesChanged(int value) => MarkDirty();

    partial void OnNotificationsChanged(NotifyMode value) { MarkDirty(); OnPropertyChanged(nameof(NotificationsHint)); }
    partial void OnRecentLimitChanged(int value) => MarkDirty();
    partial void OnQuietHoursFromChanged(int value) { MarkDirty(); OnPropertyChanged(nameof(NotificationsHint)); }
    partial void OnQuietHoursToChanged(int value) { MarkDirty(); OnPropertyChanged(nameof(NotificationsHint)); }
    partial void OnStatePathChanged(string value) { MarkDirty(); RaiseFolderPaths(); }
    partial void OnLogPathChanged(string value) { MarkDirty(); RaiseFolderPaths(); }
    partial void OnScanParallelismChanged(int value) => MarkDirty();

    partial void OnCpuLoadChanged(CpuLoad value)
    {
        OnPropertyChanged(nameof(CpuLoadHint));
        MarkDirty();
    }

    partial void OnLanguageChanged(string value)
    {
        I18n.Instance.SetLanguage(value);
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(NotificationsHint));
        OnPropertyChanged(nameof(CpuLoadHint));
        OnPropertyChanged(nameof(Languages));
        OnPropertyChanged(nameof(Themes));
        OnPropertyChanged(nameof(CpuLoads));
        OnPropertyChanged(nameof(NotifyModes));
        MarkDirty();
    }

    /// <summary>What the chosen load actually does, in the user's words.</summary>
    public string CpuLoadHint => CpuLoad switch
    {
        CpuLoad.Full => I18n.T("hint.cpuload.full"),
        CpuLoad.Low => I18n.T("hint.cpuload.low"),
        _ => I18n.T("hint.cpuload.balanced"),
    };
    partial void OnScanBufferKbChanged(int value) => MarkDirty();
    partial void OnLogKeepDaysChanged(int value) => MarkDirty();
}
