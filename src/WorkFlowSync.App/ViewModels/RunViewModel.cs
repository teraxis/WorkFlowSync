using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkFlowSync.App.Services;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.App.ViewModels;

/// <summary>«Стан» tab: last pass summary from the state database, and running a pass (dry-run or real) with a live log.</summary>
public sealed partial class RunViewModel : ObservableObject
{
    private const int MaxLogLines = 1500;

    /// <summary>
    /// A busy pass emits thousands of lines per second. Rebuilding the whole log text for each of them —
    /// even once per UI turn — kept the UI thread at 100% and Windows marked the window «не відповідає».
    /// Redraws are therefore capped at a few per second; the file log keeps every line regardless.
    /// </summary>
    private static readonly TimeSpan LogRedrawInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<SyncConfig> _configProvider;
    private readonly Func<string> _configPathProvider;
    private readonly BackgroundLoop _background;
    private readonly Func<bool> _isDirty;
    private int _activePairs;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly List<string> _logLines = new();
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isRunning;

    [ObservableProperty] private string _backgroundStatus = "";

    /// <summary>Drives the dot in the navigation rail.</summary>
    public bool IsCheckerRunning => _background.IsActive;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _lastResult = "";

    public bool CanRun => !IsRunning;

    public RunViewModel(Func<SyncConfig> configProvider, Func<string> configPathProvider, BackgroundLoop background, Func<bool> isDirty)
    {
        _configProvider = configProvider;
        _configPathProvider = configPathProvider;
        _background = background;
        _isDirty = isDirty;
        _background.Changed += OnBackgroundChanged;
        _background.LogLine += AppendLog;
    }

    /// <summary>Called whenever the pairs change: the checker follows them, so the wording follows too.</summary>
    public void RefreshBackgroundStatus(int activePairs)
    {
        _activePairs = activePairs;
        OnBackgroundChanged();
    }

    /// <summary>Called by the tray menu after it pauses or resumes the checker.</summary>
    public void SyncFromLoop() => OnBackgroundChanged();

    private void OnBackgroundChanged()
    {
        var text = _background.State switch
        {
            LoopState.Stopped when _activePairs == 0 => "Усі завдання на паузі — нічого не перевіряється",
            LoopState.Stopped => "Зупинено",
            LoopState.Paused => "Призупинено зі значка в області сповіщень",
            _ => $"{_background.StatusText} · завдань: {_activePairs}",
        };
        Set(text);
    }

    private void Set(string text) => OnUi(() =>
    {
        BackgroundStatus = text;
        OnPropertyChanged(nameof(IsCheckerRunning));
    });

    /// <summary>
    /// Updates from background threads are marshalled to the UI thread; calls already on it apply straight away
    /// (a deferred Post would leave the property stale for the caller — and for tests).
    /// </summary>
    private void OnUi(Action action)
    {
        if (_ui is null || ReferenceEquals(SynchronizationContext.Current, _ui)) action();
        else _ui.Post(_ => action(), null);
    }

    /// <summary>Reads last_run + counters from state.db without running anything.</summary>
    public void RefreshSummary()
    {
        try
        {
            var cfg = _configProvider();
            var runner = new SyncRunner(cfg, _configPathProvider(), new MemorySyncLog());
            if (!File.Exists(runner.StatePath))
            {
                Summary = "Проходів ще не було. База стану буде створена при першому запуску.";
                return;
            }
            using var store = new StateStore(runner.StatePath);
            var sb = new StringBuilder();
            var last = store.GetMeta("last_run");
            var lastLocal = last is not null && DateTimeOffset.TryParse(last, out var t) ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";
            sb.AppendLine($"Останній прохід: {lastLocal}   результат: {store.GetMeta("last_run_result") ?? "—"}");
            sb.AppendLine($"База стану: {runner.StatePath}  ({SyncExecutor.FormatSize(store.FileSizeBytes)})");
            foreach (var pair in cfg.Pairs)
            {
                var c = store.CountByStatus(pair.Name);
                int Get(EntryStatus s) => c.TryGetValue(s, out var n) ? n : 0;
                sb.AppendLine($"[{pair.Name}]  дзеркалюється: {Get(EntryStatus.Active):N0}   видалено локально (не повертати): {Get(EntryStatus.Tombstone):N0}   змінено локально: {Get(EntryStatus.LocalModified):N0}   не переноситься за віком: {Get(EntryStatus.TooOld):N0}");
            }
            Summary = sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            Summary = $"Не вдалося прочитати стан: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task DryRunAsync() => RunAsync(dryRun: true);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunNowAsync() => RunAsync(dryRun: false);

    /// <summary>Runs a single pair now, even if it is paused (the row buttons on the «Папки» tab).</summary>
    public Task RunPairAsync(string pairName) => RunAsync(dryRun: false, onlyPair: pairName);

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    private async Task RunAsync(bool dryRun, string? onlyPair = null)
    {
        if (IsRunning) return;
        var cfg = _configProvider();
        var problems = cfg.Validate();
        if (problems.Count > 0)
        {
            LastResult = "Конфігурація має помилки: " + string.Join("; ", problems);
            return;
        }

        var configPath = _configPathProvider();
        var logDir = ResolveLogDir(cfg, configPath);

        _cts = new CancellationTokenSource();
        IsRunning = true;
        lock (_logLines) _logLines.Clear();
        LogText = "";
        LastResult = dryRun ? "Розрахунок плану…" : (onlyPair is null ? "Виконується…" : $"Виконується завдання «{onlyPair}»…");

        try
        {
            var token = _cts.Token;
            // A dedicated thread of the configured priority (docs F5): on a weak PC a pass must not
            // fight the UI for the CPU, and the thread pool must keep its normal priority.
            var result = await CpuBudget.For(cfg).RunAsync(() =>
            {
                // Same cross-process lock as wfs.exe: never overlap with a scheduled or resident pass.
                using var gate = PassLock.TryAcquire(configPath);
                if (!gate.Acquired) throw new InvalidOperationException("Інший прохід уже виконується для цього конфігу (Планувальник або резидентний режим). Спробуйте пізніше.");
                using var log = new FileSyncLog(logDir, (_, line) => AppendLog(line), keepDays: cfg.LogKeepDays);
                return new SyncRunner(cfg, configPath, log).Run(dryRun, forceBackfill: false, token, onlyPair);
            }, token);

            var copied = result.Pairs.Sum(p => (p.Execution?.FilesCopied ?? 0) + (p.Execution?.FilesUpdated ?? 0));
            var planned = result.Pairs.Sum(p => (p.Plan?.New ?? 0) + (p.Plan?.Updated ?? 0));
            var skipped = result.Pairs.Count(p => p.Skipped);
            LastResult = dryRun
                ? $"План готовий: {planned:N0} файлів/тек до копіювання, пропущено завдань: {skipped}. Нічого не змінено."
                : $"Готово за {result.Elapsed.TotalSeconds:0.0} с: скопійовано/оновлено {copied:N0}, помилок {result.Errors}, пропущено завдань: {skipped}.";
        }
        catch (OperationCanceledException)
        {
            LastResult = "Перервано користувачем. Стан цього проходу не записано.";
        }
        catch (Exception ex)
        {
            LastResult = $"Помилка: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
            FlushLog();          // the throttle may still owe the last lines
            RefreshSummary();
        }
    }

    private bool _flushPending;
    private DateTime _lastFlushUtc = DateTime.MinValue;

    /// <summary>Bursts of thousands of lines are coalesced; at most one redraw per <see cref="LogRedrawInterval"/>.</summary>
    public void AppendLog(string line)
    {
        lock (_logLines)
        {
            _logLines.Add(line);
            if (_logLines.Count > MaxLogLines) _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
            if (_flushPending) return;
            _flushPending = true;
        }

        var due = _lastFlushUtc + LogRedrawInterval - DateTime.UtcNow;
        if (due <= TimeSpan.Zero)
        {
            OnUi(FlushLog);
            return;
        }
        // Wait out the rest of the window on a timer thread, never on the UI thread.
        _ = Task.Delay(due).ContinueWith(_ => OnUi(FlushLog), TaskScheduler.Default);
    }

    /// <summary>Writes whatever has accumulated; the final state is flushed again when the pass ends.</summary>
    private void FlushLog()
    {
        string text;
        lock (_logLines)
        {
            _flushPending = false;
            text = string.Join(Environment.NewLine, _logLines);
        }
        _lastFlushUtc = DateTime.UtcNow;
        LogText = text;
    }

    private static string ResolveLogDir(SyncConfig cfg, string configPath) =>
        Path.IsPathRooted(cfg.LogPath) ? cfg.LogPath : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, cfg.LogPath);

    /// <summary>
    /// Empties the log shown here and deletes the daily log files on disk. Kept apart from rotation
    /// (which drops files older than the configured window): this is the user saying «I have read it».
    /// The file the current pass is writing to stays — pulling it away would lose the line being written.
    /// </summary>
    [RelayCommand]
    private void ClearLog()
    {
        lock (_logLines) _logLines.Clear();
        LogText = "";

        var deleted = 0;
        try
        {
            var dir = ResolveLogDir(_configProvider(), _configPathProvider());
            if (Directory.Exists(dir))
            {
                var today = Path.Combine(dir, $"wfs-{DateTime.Now:yyyy-MM-dd}.log");
                foreach (var file in Directory.EnumerateFiles(dir, "wfs-*.log"))
                {
                    if (string.Equals(file, today, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(file); deleted++; } catch (IOException) { /* held open: rotation will get it */ }
                }
            }
            LastResult = deleted > 0 ? $"Журнал очищено, видалено файлів: {deleted}" : "Журнал очищено";
        }
        catch (Exception ex)
        {
            LastResult = $"Не вдалося видалити файли журналу: {ex.Message}";
        }
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        DryRunCommand.NotifyCanExecuteChanged();
        RunNowCommand.NotifyCanExecuteChanged();
    }
}
