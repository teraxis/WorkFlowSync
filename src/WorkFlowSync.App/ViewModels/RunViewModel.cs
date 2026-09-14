using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private const int MaxLogLines = 3000;

    private readonly Func<SyncConfig> _configProvider;
    private readonly Func<string> _configPathProvider;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly List<string> _logLines = new();
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _lastResult = "";

    public bool CanRun => !IsRunning;

    public RunViewModel(Func<SyncConfig> configProvider, Func<string> configPathProvider)
    {
        _configProvider = configProvider;
        _configPathProvider = configPathProvider;
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
                sb.AppendLine($"[{pair.Name}]  дзеркалюється: {Get(EntryStatus.Active):N0}   видалено локально (не повертати): {Get(EntryStatus.Tombstone):N0}   змінено локально: {Get(EntryStatus.LocalModified):N0}");
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

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    private async Task RunAsync(bool dryRun)
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
        var logDir = Path.IsPathRooted(cfg.LogPath) ? cfg.LogPath : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, cfg.LogPath);

        _cts = new CancellationTokenSource();
        IsRunning = true;
        lock (_logLines) _logLines.Clear();
        LogText = "";
        LastResult = dryRun ? "Розрахунок плану…" : "Виконується…";

        try
        {
            var token = _cts.Token;
            var result = await Task.Run(() =>
            {
                using var log = new FileSyncLog(logDir, (_, line) => AppendLog(line));
                return new SyncRunner(cfg, configPath, log).Run(dryRun, forceBackfill: false, token);
            }, _cts.Token);

            var copied = result.Pairs.Sum(p => (p.Execution?.FilesCopied ?? 0) + (p.Execution?.FilesUpdated ?? 0));
            var planned = result.Pairs.Sum(p => (p.Plan?.New ?? 0) + (p.Plan?.Updated ?? 0));
            var skipped = result.Pairs.Count(p => p.Skipped);
            LastResult = dryRun
                ? $"План готовий: {planned:N0} файлів/папок до копіювання, пропущено пар: {skipped}. Нічого не змінено."
                : $"Готово за {result.Elapsed.TotalSeconds:0.0} с: скопійовано/оновлено {copied:N0}, помилок {result.Errors}, пропущено пар: {skipped}.";
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
            RefreshSummary();
        }
    }

    private bool _flushPending;

    /// <summary>Bursts of thousands of lines are coalesced into one UI update per UI-thread turn.</summary>
    private void AppendLog(string line)
    {
        lock (_logLines)
        {
            _logLines.Add(line);
            if (_logLines.Count > MaxLogLines) _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
            if (_flushPending) return;
            _flushPending = true;
        }
        if (_ui is not null) _ui.Post(_ => FlushLog(), null);
        else FlushLog();
    }

    private void FlushLog()
    {
        string text;
        lock (_logLines)
        {
            _flushPending = false;
            text = string.Join(Environment.NewLine, _logLines);
        }
        LogText = text;
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        DryRunCommand.NotifyCanExecuteChanged();
        RunNowCommand.NotifyCanExecuteChanged();
    }
}
