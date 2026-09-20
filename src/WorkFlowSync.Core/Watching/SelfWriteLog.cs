using System.Collections.Concurrent;

namespace WorkFlowSync.Core.Watching;

/// <summary>
/// Remembers what this program has just written, so the watcher can ignore its own footsteps
/// (docs/plan-etap5.md §4.3, rule 7).
///
/// Without it a pair ping-pongs: the pass writes into the mirror, the watcher sees a change, schedules
/// another pass, which writes again. Entries expire on their own — a note that is never claimed must not
/// suppress a real change made minutes later at the same path.
/// </summary>
public sealed class SelfWriteLog
{
    /// <summary>
    /// How long a write stays "ours". One write produces SEVERAL notifications — a single file creation was
    /// measured producing six on an SMB share — so the note has to outlive all of them, which is why it is
    /// not consumed on first use. The cost is a blind spot: if the user edits the very same file within the
    /// window, that edit waits for the next full pass. Kept short for exactly that reason.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _written = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;

    public SelfWriteLog(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public int Count => _written.Count;

    /// <summary>Called right after the executor writes, renames or removes something.</summary>
    public void Record(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        _written[Normalise(fullPath)] = _clock();
        if (_written.Count > 4096) Prune();     // a long pass must not grow this without bound
    }

    /// <summary>
    /// True while this path is still one of ours. The note is NOT consumed — a single write arrives as
    /// several notifications, and consuming it would let every event after the first through, which is
    /// exactly the ping-pong this class exists to prevent (caught by a test, not by review).
    /// </summary>
    public bool WasOurs(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;
        var key = Normalise(fullPath);
        if (!_written.TryGetValue(key, out var when)) return false;

        if (_clock() - when <= Window) return true;

        _written.TryRemove(key, out _);
        return false;
    }

    public void Prune()
    {
        var cutoff = _clock() - Window;
        foreach (var (path, when) in _written)
            if (when < cutoff)
                _written.TryRemove(path, out _);
    }

    public void Clear() => _written.Clear();

    private static string Normalise(string path) => path.TrimEnd('\\', '/');
}
