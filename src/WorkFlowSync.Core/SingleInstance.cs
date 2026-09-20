using System.Security.Cryptography;
using System.Text;

namespace WorkFlowSync.Core;

/// <summary>
/// One window per config file. A second launch does not open a second copy: it wakes the one already
/// running and exits, the way a tray application is expected to behave.
///
/// Keyed by the config path rather than globally, because the product is portable: two folders with their
/// own config.json are two independent installations and must be able to run side by side. Two launches of
/// the SAME folder are the case this prevents — otherwise both copies would run passes against one state
/// database, each holding the other off with <see cref="PassLock"/>, and settings saved in one window would
/// be silently overwritten by the other.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly HashSet<string> HeldInProcess = new(StringComparer.Ordinal);

    /// <summary>The lock this process holds, for the parts of the app that start after the entry point.</summary>
    public static SingleInstance? Current { get; private set; }

    private readonly string _name;
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _wakeUp;

    private SingleInstance(string name, Mutex? mutex, EventWaitHandle? wakeUp, bool isFirst)
    {
        _name = name;
        _mutex = mutex;
        _wakeUp = wakeUp;
        IsFirst = isFirst;
    }

    /// <summary>False when another copy already owns this config — the caller should signal it and exit.</summary>
    public bool IsFirst { get; }

    private static string Key(string configPath) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(configPath).ToUpperInvariant())))[..16];

    // Distinct from PassLock's name on purpose: that one is held only while a pass runs, this one for the
    // whole life of the process. Sharing a name would make a running window look like a pass in progress.
    public static string MutexNameFor(string configPath) => $"Local\\WorkFlowSync.app.{Key(configPath)}";

    private static string EventNameFor(string configPath) => $"Local\\WorkFlowSync.show.{Key(configPath)}";

    /// <summary>Claims this config for the current process. Dispose releases it.</summary>
    public static SingleInstance TryAcquire(string configPath)
    {
        var name = MutexNameFor(configPath);
        lock (HeldInProcess)
        {
            if (HeldInProcess.Contains(name))
                return new SingleInstance(name, null, null, isFirst: false);

            var mutex = new Mutex(false, name);
            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owned = true;   // the previous window was killed; nothing shared is left in a bad state
            }

            if (!owned)
            {
                mutex.Dispose();
                return new SingleInstance(name, null, null, isFirst: false);
            }

            // Created by the first instance only, so a second launch that finds no event knows nobody is listening.
            var wakeUp = new EventWaitHandle(false, EventResetMode.AutoReset, EventNameFor(configPath));
            HeldInProcess.Add(name);
            var instance = new SingleInstance(name, mutex, wakeUp, isFirst: true);
            Current = instance;
            return instance;
        }
    }

    /// <summary>
    /// Asks the copy that is already running to show itself. Returns false when there is nothing listening —
    /// then the caller is free to start normally.
    /// </summary>
    public static bool SignalExisting(string configPath)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(EventNameFor(configPath), out var handle)) return false;
            using (handle) return handle.Set();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;   // never let a failed hand-off keep the user from their program
        }
    }

    /// <summary>
    /// Runs <paramref name="onSignal"/> whenever another launch asks for the window. The callback arrives on a
    /// background thread — marshal to the UI thread yourself.
    /// </summary>
    public void ListenForOtherLaunches(Action onSignal, CancellationToken ct = default)
    {
        if (!IsFirst || _wakeUp is null) return;

        var thread = new Thread(() =>
        {
            var handles = new[] { _wakeUp, ct.WaitHandle };
            while (!ct.IsCancellationRequested)
            {
                int signalled;
                try
                {
                    signalled = WaitHandle.WaitAny(handles);
                }
                catch (ObjectDisposedException)
                {
                    return;     // shutting down
                }
                if (signalled != 0) return;
                try
                {
                    onSignal();
                }
                catch
                {
                    // A failure to raise the window must never take the running program down with it.
                }
            }
        })
        {
            IsBackground = true,
            Name = "wfs-single-instance",
        };
        thread.Start();
    }

    public void Dispose()
    {
        if (!IsFirst) return;
        lock (HeldInProcess)
        {
            HeldInProcess.Remove(_name);
            if (ReferenceEquals(Current, this)) Current = null;
            _wakeUp?.Dispose();
            try { _mutex!.ReleaseMutex(); } catch { /* released from another thread; the handle close frees it */ }
            _mutex!.Dispose();
        }
    }
}
