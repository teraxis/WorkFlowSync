using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkFlowSync.App.Services;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.App.ViewModels;

/// <summary>
/// Edits config.json: folder pairs + settings. No Avalonia types here (dialogs live in the window code-behind)
/// so the mapping to <see cref="SyncConfig"/> stays unit-testable.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public string ConfigPath { get; }
    public string Title => $"{WorkFlowSyncInfo.Name} {WorkFlowSyncInfo.Version}";
    public string Version => $"версія {WorkFlowSyncInfo.Version}";

    public ObservableCollection<PairViewModel> Pairs { get; } = new();

    [ObservableProperty] private PairViewModel? _selectedPair;

    /// <summary>Which page the navigation rail shows: 0 folders, 1 activity, 2 settings.</summary>
    [ObservableProperty] private int _selectedPage;

    public bool IsFoldersPage => SelectedPage == 0;
    public bool IsActivityPage => SelectedPage == 1;
    public bool IsSettingsPage => SelectedPage == 2;

    public string PageTitle => SelectedPage switch
    {
        0 => "Папки",
        1 => "Синхронізація",
        _ => "Налаштування",
    };

    public IReadOnlyList<AppTheme> Themes { get; } = new[] { AppTheme.System, AppTheme.Light, AppTheme.Dark };

    /// <summary>Applied immediately and remembered in the config.</summary>
    [ObservableProperty] private AppTheme _theme = AppTheme.System;

    // Settings tab
    [ObservableProperty] private int _intervalMinutes = 30;
    [ObservableProperty] private string _statePath = "state.db";
    [ObservableProperty] private string _logPath = "logs";
    [ObservableProperty] private int _scanParallelism = 8;
    [ObservableProperty] private int _scanBufferKb = 256;

    [ObservableProperty] private bool _isDirty;
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
        Background = new BackgroundLoop(ConfigPath, ResolveLogDir);
        Run = new RunViewModel(ToConfig, () => ConfigPath, Background, () => IsDirty);
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
            SetStatus(created ? $"Створено config.json у папці програми: {AppFolder}" : "", error: false);
            IsDirty = false;
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
        foreach (var p in cfg.Pairs) Pairs.Add(PairViewModel.FromModel(p));
        IntervalMinutes = Math.Max(1, (int)Math.Round(cfg.Interval.TotalMinutes));
        StatePath = cfg.StatePath;
        LogPath = cfg.LogPath;
        ScanParallelism = cfg.ScanParallelism;
        ScanBufferKb = Math.Max(4, cfg.ScanBufferSize / 1024);
        Theme = cfg.Theme;
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
        Interval = TimeSpan.FromMinutes(IntervalMinutes),
        StatePath = StatePath.Trim(),
        LogPath = LogPath.Trim(),
        ScanParallelism = ScanParallelism,
        ScanBufferSize = ScanBufferKb * 1024,
        Theme = Theme,
    };

    /// <summary>Returns validation problems (empty = OK) and reflects them in the status line.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = ToConfig().Validate();
        SetStatus(problems.Count == 0 ? "Конфігурація коректна" : string.Join("  •  ", problems), error: problems.Count > 0);
        return problems;
    }

    [RelayCommand]
    private void Save()
    {
        var problems = Validate();
        if (problems.Count > 0) return;
        try
        {
            ConfigFile.Save(ToConfig(), ConfigPath);
            IsDirty = false;
            SetStatus($"Збережено {ConfigPath}", error: false);
            FollowPairStates();
        }
        catch (Exception ex)
        {
            SetStatus($"Не вдалося зберегти: {ex.Message}", error: true);
        }
    }

    [RelayCommand]
    private void CheckConfig() => Validate();

    [RelayCommand]
    private void Reload() => Load();

    /// <summary>Adds a pair prepared by the dialog.</summary>
    public void AddPair(PairViewModel pair)
    {
        pair.RaiseSummaries();
        Pairs.Add(pair);
        SelectedPair = pair;
        MarkDirty();
        FollowPairStates();
    }

    public void ReplacePair(PairViewModel target, PairViewModel edited)
    {
        target.CopyFrom(edited);
        MarkDirty();
    }

    /// <summary>Removes a pair from the list (its state rows stay, so re-adding it keeps the memory).</summary>
    public void RemovePair(PairViewModel pair)
    {
        Pairs.Remove(pair);
        if (ReferenceEquals(SelectedPair, pair)) SelectedPair = null;
        MarkDirty();
        FollowPairStates();
        SetStatus($"Пару «{pair.Name}» видалено зі списку. Натисніть «Зберегти», щоб застосувати.", error: false);
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedPair is null) return;
        Pairs.Remove(SelectedPair);
        SelectedPair = null;
        MarkDirty();
        FollowPairStates();
    }

    public string SuggestPairName(string source)
    {
        // Last path segment; Path.GetFileName returns "" for a share root like \\srv\share, so split by hand.
        var baseName = source.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        baseName = baseName.TrimEnd(':');
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "pair";
        var name = baseName;
        var i = 2;
        while (Pairs.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName}-{i++}";
        return name;
    }

    /// <summary>
    /// Imports a FreeFileSync batch: wildcard excludes go into the matching pair (unsaved until «Зберегти»),
    /// concrete paths become tombstones in the state database immediately. Returns a human-readable summary.
    /// </summary>
    public string ImportFreeFileSync(string batchPath)
    {
        var batch = Core.Ffs.FfsBatch.Load(batchPath);
        if (batch.Pairs.Count == 0) return "У файлі FreeFileSync немає пар папок.";

        var cfg = ToConfig();
        var runner = new SyncRunner(cfg, ConfigPath, new Core.Logging.MemorySyncLog());
        using var store = new Core.State.StateStore(runner.StatePath);
        var now = DateTimeOffset.UtcNow;
        var lines = new List<string>();
        var anyPatterns = false;

        foreach (var ffsPair in batch.Pairs)
        {
            var target = Core.Ffs.FfsBatchImporter.MatchPair(cfg, ffsPair);
            if (target is null)
            {
                lines.Add($"Пропущено {ffsPair.Left}: додайте пару з таким джерелом і повторіть імпорт.");
                continue;
            }
            var r = Core.Ffs.FfsBatchImporter.Prepare(batch, ffsPair, target, store.Load(target.Name), now, probeSource: true);
            if (r.Rows.Count > 0) store.Upsert(r.Rows);
            if (r.NewPatterns.Count > 0)
            {
                var vm = Pairs.First(p => p.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
                vm.ExcludeText = string.Join(Environment.NewLine, vm.ExcludeText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Concat(r.NewPatterns));
                vm.RaiseSummaries();
                anyPatterns = true;
            }
            lines.Add($"[{target.Name}] шаблонів +{r.Patterns}, «не повертати» +{r.Tombstones:N0} (уже було {r.TombstonesAlreadyPresent:N0}).");
        }
        if (anyPatterns) MarkDirty();
        Run.RefreshSummary();
        var text = string.Join(" ", lines) + (anyPatterns ? " Натисніть «Зберегти», щоб зберегти нові шаблони." : "");
        SetStatus(text, error: false);
        return text;
    }

    /// <summary>
    /// The row's play/pause button. Applies at once: the new state goes into config.json (the checker reads it from
    /// disk), the checker starts or stops accordingly, and a freshly started pair is checked immediately.
    /// </summary>
    public async Task TogglePairAsync(PairViewModel pair)
    {
        pair.Enabled = !pair.Enabled;
        var saved = PersistPairStates();
        FollowPairStates();

        if (!saved)
        {
            SetStatus(pair.Enabled
                ? $"«{pair.Name}» — виконується. Є інші незбережені зміни: натисніть «Зберегти»."
                : $"«{pair.Name}» — на паузі. Є інші незбережені зміни: натисніть «Зберегти».", error: false);
            return;
        }

        if (pair.Enabled)
        {
            SetStatus($"«{pair.Name}» — виконується: перевіряю зараз, далі кожні {IntervalMinutes} хв.", error: false);
            await RunPairAsync(pair);
        }
        else
        {
            SetStatus($"«{pair.Name}» — на паузі: автоматичні перевірки цієї пари зупинено.", error: false);
        }
    }

    /// <summary>Writes the play/pause states to config.json. Returns false when other unsaved edits block it.</summary>
    private bool PersistPairStates()
    {
        if (IsDirty) return false;
        try
        {
            ConfigFile.Save(ToConfig(), ConfigPath);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"Не вдалося зберегти стан пари: {ex.Message}", error: true);
            return false;
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

    public void MarkDirty()
    {
        IsDirty = true;
        SetStatus("Є незбережені зміни", error: false);
    }

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
        if (_loading) return;
        MarkDirty();
    }

    /// <summary>Raised when the theme must be applied to the running application (wired in App.axaml.cs).</summary>
    public event Action<AppTheme>? ThemeApplied;

    partial void OnSelectedPairChanged(PairViewModel? value) => OnPropertyChanged(nameof(HasSelection));
    partial void OnIntervalMinutesChanged(int value) => MarkDirty();
    partial void OnStatePathChanged(string value) { MarkDirty(); RaiseFolderPaths(); }
    partial void OnLogPathChanged(string value) { MarkDirty(); RaiseFolderPaths(); }
    partial void OnScanParallelismChanged(int value) => MarkDirty();
    partial void OnScanBufferKbChanged(int value) => MarkDirty();
}
