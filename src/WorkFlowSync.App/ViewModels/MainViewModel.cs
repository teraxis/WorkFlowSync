using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public ObservableCollection<PairViewModel> Pairs { get; } = new();

    [ObservableProperty] private PairViewModel? _selectedPair;

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

    public MainViewModel(string configPath)
    {
        ConfigPath = configPath;
        Pairs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPairs));
        Run = new RunViewModel(ToConfig, () => ConfigPath);
        Load();
        Run.RefreshSummary();
    }

    public void Load()
    {
        try
        {
            var cfg = ConfigFile.LoadOrDefault(ConfigPath);
            Apply(cfg);
            SetStatus(File.Exists(ConfigPath)
                ? $"Завантажено {ConfigPath}"
                : $"Конфіг ще не створено — буде збережено як {ConfigPath}", error: false);
            IsDirty = false;
        }
        catch (Exception ex)
        {
            SetStatus($"Не вдалося прочитати конфіг: {ex.Message}", error: true);
        }
    }

    private void Apply(SyncConfig cfg)
    {
        Pairs.Clear();
        foreach (var p in cfg.Pairs) Pairs.Add(PairViewModel.FromModel(p));
        IntervalMinutes = Math.Max(1, (int)Math.Round(cfg.Interval.TotalMinutes));
        StatePath = cfg.StatePath;
        LogPath = cfg.LogPath;
        ScanParallelism = cfg.ScanParallelism;
        ScanBufferKb = Math.Max(4, cfg.ScanBufferSize / 1024);
    }

    public SyncConfig ToConfig() => new()
    {
        Pairs = Pairs.Select(p => p.ToModel()).ToList(),
        Interval = TimeSpan.FromMinutes(IntervalMinutes),
        StatePath = StatePath.Trim(),
        LogPath = LogPath.Trim(),
        ScanParallelism = ScanParallelism,
        ScanBufferSize = ScanBufferKb * 1024,
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
    }

    public void ReplacePair(PairViewModel target, PairViewModel edited)
    {
        target.CopyFrom(edited);
        MarkDirty();
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedPair is null) return;
        Pairs.Remove(SelectedPair);
        SelectedPair = null;
        MarkDirty();
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

    partial void OnSelectedPairChanged(PairViewModel? value) => OnPropertyChanged(nameof(HasSelection));
    partial void OnIntervalMinutesChanged(int value) => MarkDirty();
    partial void OnStatePathChanged(string value) => MarkDirty();
    partial void OnLogPathChanged(string value) => MarkDirty();
    partial void OnScanParallelismChanged(int value) => MarkDirty();
    partial void OnScanBufferKbChanged(int value) => MarkDirty();
}
