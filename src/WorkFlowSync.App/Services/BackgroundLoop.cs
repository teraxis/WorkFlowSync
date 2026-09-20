using WorkFlowSync.Core;
using WorkFlowSync.Core.I18n;
using WorkFlowSync.Core.Logging;

namespace WorkFlowSync.App.Services;

public enum LoopState { Stopped, Idle, Running, Paused }

/// <summary>
/// The resident loop hosted inside the GUI (tray mode): same <see cref="LoopRunner"/> the console uses,
/// plus pause/resume and a status line for the tray tooltip.
/// </summary>
public sealed class BackgroundLoop : IDisposable
{
    private readonly string _configPath;
    private readonly Func<string> _logDir;
    private readonly Func<int> _logKeepDays;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private WorkFlowSync.Core.LoopRunner? _loop;

    public LoopState State { get; private set; } = LoopState.Stopped;
    public DateTimeOffset? LastPassUtc { get; private set; }
    public int LastPassErrors { get; private set; }
    public int LastPassCopied { get; private set; }
    public bool HasHadPass { get; private set; }

    public string LastResult => !HasHadPass
        ? ""
        : (LastPassErrors > 0
            ? I18n.T("loop.errors", LastPassErrors)
            : I18n.T("loop.copied_updated", LastPassCopied.ToString("N0", I18n.Instance.Culture)));

    public event Action? Changed;

    /// <summary>
    /// Raised after every pass, scheduled or partial, with what it did — the tray uses it to decide
    /// whether the user is worth interrupting. Fires on the loop's own thread.
    /// </summary>
    public event Action<WorkFlowSync.Core.PassResult>? PassCompleted;

    /// <summary>Every log line of a background pass, so the window can show it live.</summary>
    public event Action<string>? LogLine;

    public BackgroundLoop(string configPath, Func<string> logDir, Func<int>? logKeepDays = null)
    {
        _configPath = configPath;
        _logDir = logDir;
        _logKeepDays = logKeepDays ?? (() => FileSyncLog.KeepDays);

        I18n.Instance.LanguageChanged += () => Changed?.Invoke();
    }

    public bool IsActive => State is LoopState.Idle or LoopState.Running;

    public void Start()
    {
        if (IsActive && _task is { IsCompleted: false }) return;
        CancelAndWait();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Set(LoopState.Idle);
        _task = RunOnOwnThread(async () =>
        {
            using var log = new FileSyncLog(_logDir(), (_, line) => LogLine?.Invoke(line), keepDays: _logKeepDays());
            var loop = new LoopRunner(_configPath, log);
            _loop = loop;   // only to read its watching state; the loop itself is driven by the task below
            loop.PassStarting += () => Set(LoopState.Running);
            loop.PassCompleted += r =>
            {
                LastPassUtc = DateTimeOffset.UtcNow;
                LastPassCopied = r.Pairs.Sum(p => (p.Execution?.FilesCopied ?? 0) + (p.Execution?.FilesUpdated ?? 0));
                LastPassErrors = r.Errors;
                HasHadPass = true;
                Set(LoopState.Idle);
                PassCompleted?.Invoke(r);
            };
            try
            {
                await loop.RunAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on Stop()
            }
            finally
            {
                if (State != LoopState.Paused) Set(LoopState.Stopped);
            }
        });
    }

    /// <summary>
    /// The loop runs on a dedicated background thread, not on the thread pool: a pass lowers the priority
    /// of the thread it runs on (docs F5), and that must not leak into pool threads the UI also uses.
    /// </summary>
    private static Task RunOnOwnThread(Func<Task> work)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { work().GetAwaiter().GetResult(); tcs.TrySetResult(); }
            catch (OperationCanceledException) { tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        })
        {
            IsBackground = true,
            Name = "wfs-loop",
        };
        thread.Start();
        return tcs.Task;
    }

    public void Stop()
    {
        CancelAndWait();
        Set(LoopState.Stopped);
    }

    public void Pause()
    {
        if (State == LoopState.Paused) return;
        Set(LoopState.Paused);      // set first: the worker's finally must not overwrite it with Stopped
        CancelAndWait();
        Set(LoopState.Paused);
    }

    private readonly object _syncLock = new();

    /// <summary>Cancels the worker and gives it a moment to unwind, so a following Start() is not a no-op.</summary>
    private void CancelAndWait()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_syncLock)
        {
            cts = _cts;
            task = _task;
            _cts = null;
            _task = null;
        }

        if (cts is null) return;
        try { cts.Cancel(); } catch { /* ignore */ }
        try { task?.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancellation surfaces here */ }
        try { cts.Dispose(); } catch { /* ignore */ }
    }

    public void Resume()
    {
        if (State != LoopState.Paused) return;
        Start();
    }

    /// <summary>
    /// How many folders are being watched for changes right now, so the window can say whether the pair
    /// reacts in seconds or waits for the interval. 0 also means "not watching", which is worth showing.
    /// </summary>
    public int WatchedRoots => _loop?.ActiveWatchers ?? 0;

    private string WatchSuffix => WatchedRoots > 0 ? I18n.T("loop.watching_suffix", WatchedRoots) : "";

    public string StatusText => State switch
    {
        LoopState.Idle => LastPassUtc is { } t
            ? I18n.T("loop.status_idle_last", t.ToLocalTime().ToString("HH:mm", I18n.Instance.Culture), LastResult, WatchSuffix)
            : I18n.T("loop.status_idle_first"),
        LoopState.Running => I18n.T("loop.status_running"),
        LoopState.Paused => I18n.T("loop.status_paused"),
        _ => I18n.T("loop.status_stopped"),
    };

    private void Set(LoopState state)
    {
        State = state;
        Changed?.Invoke();
    }

    public void Dispose() => CancelAndWait();
}
