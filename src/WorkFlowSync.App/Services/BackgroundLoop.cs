using WorkFlowSync.Core;
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
    private readonly Action<string>? _onLogLine;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public LoopState State { get; private set; } = LoopState.Stopped;
    public DateTimeOffset? LastPassUtc { get; private set; }
    public string LastResult { get; private set; } = "";

    public event Action? Changed;

    public BackgroundLoop(string configPath, Func<string> logDir, Action<string>? onLogLine = null)
    {
        _configPath = configPath;
        _logDir = logDir;
        _onLogLine = onLogLine;
    }

    public bool IsActive => State is LoopState.Idle or LoopState.Running;

    public void Start()
    {
        if (IsActive && _task is { IsCompleted: false }) return;
        CancelAndWait();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Set(LoopState.Idle);
        _task = Task.Run(async () =>
        {
            using var log = new FileSyncLog(_logDir(), (_, line) => _onLogLine?.Invoke(line));
            var loop = new LoopRunner(_configPath, log);
            loop.PassStarting += () => Set(LoopState.Running);
            loop.PassCompleted += r =>
            {
                LastPassUtc = DateTimeOffset.UtcNow;
                var copied = r.Pairs.Sum(p => (p.Execution?.FilesCopied ?? 0) + (p.Execution?.FilesUpdated ?? 0));
                LastResult = r.Errors > 0 ? $"помилок: {r.Errors}" : $"скопійовано/оновлено: {copied:N0}";
                Set(LoopState.Idle);
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
        }, token);
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

    /// <summary>Cancels the worker and gives it a moment to unwind, so a following Start() is not a no-op.</summary>
    private void CancelAndWait()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancellation surfaces here */ }
        _cts.Dispose();
        _cts = null;
        _task = null;
    }

    public void Resume()
    {
        if (State != LoopState.Paused) return;
        Start();
    }

    public string StatusText => State switch
    {
        LoopState.Idle => LastPassUtc is { } t
            ? $"Фоновий режим: очікування. Останній прохід {t.ToLocalTime():HH:mm}, {LastResult}"
            : "Фоновий режим: очікування першого проходу",
        LoopState.Running => "Фоновий режим: виконується прохід…",
        LoopState.Paused => "Фоновий режим: на паузі",
        _ => "Фоновий режим: вимкнено",
    };

    private void Set(LoopState state)
    {
        State = state;
        Changed?.Invoke();
    }

    public void Dispose() => CancelAndWait();
}
