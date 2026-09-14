using System.Security.Cryptography;
using System.Text;

namespace WorkFlowSync.Core;

/// <summary>
/// One pass at a time per config file, across processes (GUI «Виконати зараз», wfs.exe from Task Scheduler,
/// a resident --loop): a named mutex derived from the config path. A Windows mutex is re-entrant for the owning
/// thread, so in-process ownership is tracked separately to keep the lock exclusive inside one process as well.
/// </summary>
public sealed class PassLock : IDisposable
{
    private static readonly HashSet<string> HeldInProcess = new(StringComparer.Ordinal);

    private readonly string _name;
    private readonly Mutex? _mutex;
    private readonly bool _owned;

    private PassLock(string name, Mutex? mutex, bool owned)
    {
        _name = name;
        _mutex = mutex;
        _owned = owned;
    }

    public bool Acquired => _owned;

    public static string NameFor(string configPath)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(configPath).ToUpperInvariant())));
        return $"Local\\WorkFlowSync.{hash[..16]}";
    }

    /// <summary>Returns a lock whose <see cref="Acquired"/> tells whether this caller may run a pass now.</summary>
    public static PassLock TryAcquire(string configPath)
    {
        var name = NameFor(configPath);
        lock (HeldInProcess)
        {
            if (HeldInProcess.Contains(name))
                return new PassLock(name, null, owned: false);

            var mutex = new Mutex(false, name);
            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owned = true;   // previous holder died mid-pass; the state DB is transactional, so carry on
            }
            if (!owned)
            {
                mutex.Dispose();
                return new PassLock(name, null, owned: false);
            }
            HeldInProcess.Add(name);
            return new PassLock(name, mutex, owned: true);
        }
    }

    public void Dispose()
    {
        if (!_owned) return;
        lock (HeldInProcess)
        {
            HeldInProcess.Remove(_name);
            try { _mutex!.ReleaseMutex(); } catch { /* released on a different thread: the handle close below still frees it */ }
            _mutex!.Dispose();
        }
    }
}
