using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Scanning;

namespace WorkFlowSync.Core.Watching;

/// <summary>
/// Watches one root and feeds a <see cref="ChangeQueue"/> (docs/plan-etap5.md §4.3).
///
/// Watching a whole tree costs ONE kernel subscription — fifty thousand folders are no more expensive than
/// one. What is limited is the event buffer: 64 KB is roughly 460 events, so a bulk copy overflows it as a
/// matter of course. That is not an error condition to hide; it is the normal path, and the answer to it is
/// always the same — ask for a full pass.
///
/// Everything this class knows is a HINT. The periodic full pass stays the source of truth.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    /// <summary>The largest buffer Windows will honour; anything more is silently ignored.</summary>
    public const int MaxBufferBytes = 64 * 1024;

    private readonly string _root;
    private readonly ChangeQueue _queue;
    private readonly SelfWriteLog? _selfWrites;
    private readonly ISyncLog _log;
    private readonly string _tag;

    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public FolderWatcher(string root, ChangeQueue queue, ISyncLog log, string tag = "", SelfWriteLog? selfWrites = null)
    {
        _root = Path.GetFullPath(root).TrimEnd('\\', '/');
        _queue = queue;
        _log = log;
        _tag = tag;
        _selfWrites = selfWrites;
    }

    /// <summary>False when the subscription could not be created; the caller then simply relies on the interval.</summary>
    public bool Active => _watcher is { EnableRaisingEvents: true };

    /// <summary>Events seen since the last <see cref="Start"/>; diagnostics only.</summary>
    public long EventsSeen { get; private set; }

    /// <summary>How many times the subscription had to be rebuilt (overflow, disconnect, medium removed).</summary>
    public int Restarts { get; private set; }

    /// <summary>
    /// Subscribes. Returns false when this root cannot be watched at all — an offline share, a path that
    /// vanished, a provider without notification support — which is a fact to log, not a failure to throw.
    /// </summary>
    public bool Start()
    {
        Stop();
        try
        {
            var watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = MaxBufferBytes,
                // Attributes are left out deliberately: OneDrive hydrating a placeholder changes them, and
                // that is not a change to the file (docs §4.3, rule 5).
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            // Starting does NOT ask for a full pass by itself. The caller decides: the resident loop starts
            // watching immediately after a full pass, so there is no gap to cover, and asking here made the
            // loop run a second full pass at once — which then did the work the watcher was supposed to do
            // (caught by the integration tests). Re-subscribing after an error is different: see OnError.
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"{_tag} cannot watch {_root}: {ex.GetType().Name}: {ex.Message} — falling back to the interval");
            _watcher = null;
            return false;
        }
    }

    public void Stop()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null) return;
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnChanged;
            watcher.Changed -= OnChanged;
            watcher.Deleted -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException)
        {
            // Already gone; nothing to unhook.
        }
        watcher.Dispose();
    }

    // ------------------------------------------------------------------ handlers
    // These run on thread-pool threads while the kernel buffer keeps filling. They do exactly one thing.

    private void OnChanged(object sender, FileSystemEventArgs e) => Record(e.Name);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Both ends matter: the old name disappeared from its folder and the new one appeared in its own.
        Record(e.OldName);
        Record(e.Name);
    }

    private void Record(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return;
        EventsSeen++;

        // Our own writes come back to us as events; reacting to them would start a pass that writes again.
        if (_selfWrites is not null && _selfWrites.WasOurs(Path.Combine(_root, relativePath))) return;

        _queue.Add(relativePath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        // The usual cause is the buffer overflowing during a bulk copy: events were dropped, and there is no
        // way to learn which. Anything else — a dropped SMB session, an unplugged drive — is just as blind.
        _log.Warn($"{_tag} watching interrupted ({ex.GetType().Name}: {ex.Message}); a full pass will be run");
        _queue.RequestFullPass($"watching interrupted: {ex.GetType().Name}");

        Restarts++;
        if (_disposed) return;
        if (!Start()) _log.Warn($"{_tag} could not resume watching {_root}; the interval keeps the pair up to date");
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
