using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WorkFlowSync.Core;

/// <summary>
/// Cross-process pass lock: per-pair or per-config (GUI «Виконати зараз», wfs.exe from Task Scheduler,
/// a resident --loop). A named mutex is derived from the config path and optional pair name.
/// Windows mutexes are re-entrant for the owning thread, so in-process ownership is tracked separately.
/// </summary>
public sealed class PassLock : IDisposable
{
    private static readonly HashSet<string> HeldInProcess = new(StringComparer.Ordinal);

    private readonly List<string> _names = new();
    private readonly List<Mutex> _mutexes = new();
    private readonly bool _owned;

    private PassLock(bool owned)
    {
        _owned = owned;
    }

    private PassLock(string name, Mutex mutex)
    {
        _names.Add(name);
        _mutexes.Add(mutex);
        _owned = true;
    }

    private PassLock(IEnumerable<string> names, IEnumerable<Mutex> mutexes)
    {
        _names.AddRange(names);
        _mutexes.AddRange(mutexes);
        _owned = true;
    }

    public bool Acquired => _owned;

    public static string NameFor(string configPath) => NameFor(configPath, null);

    public static string NameFor(string configPath, string? pairName)
    {
        var cfgNorm = Path.GetFullPath(configPath).ToUpperInvariant();
        var cfgHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(cfgNorm)))[..16];
        if (string.IsNullOrWhiteSpace(pairName))
        {
            return $"Local\\WorkFlowSync.{cfgHash}";
        }
        var pairNorm = pairName.Trim().ToUpperInvariant();
        var pairHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(pairNorm)))[..16];
        return $"Local\\WorkFlowSync.{cfgHash}.{pairHash}";
    }

    public static bool IsGlobalHeld(string configPath)
    {
        var globalName = NameFor(configPath, null);
        lock (HeldInProcess)
        {
            if (HeldInProcess.Contains(globalName)) return true;
        }

        try
        {
            using var mutex = new Mutex(false, globalName);
            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            if (!owned) return true;
            mutex.ReleaseMutex();
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns a lock for the given config and optional pair name.
    /// If <paramref name="pairName"/> is null, an exclusive global lock across all pairs is acquired.
    /// If <paramref name="pairName"/> is provided, an exclusive lock for that pair is acquired,
    /// allowing other pairs to run concurrently.
    /// </summary>
    public static PassLock TryAcquire(string configPath, string? pairName = null)
    {
        var globalName = NameFor(configPath, null);

        if (string.IsNullOrWhiteSpace(pairName))
        {
            return TryAcquireGlobal(configPath, globalName);
        }

        return TryAcquirePair(configPath, pairName, globalName);
    }

    private static PassLock TryAcquireGlobal(string configPath, string globalName)
    {
        lock (HeldInProcess)
        {
            // In process: any lock belonging to this config blocks the global pass
            if (HeldInProcess.Any(n => n.StartsWith(globalName, StringComparison.Ordinal)))
                return new PassLock(owned: false);

            var acquiredMutexes = new List<Mutex>();
            var acquiredNames = new List<string>();

            var globalMutex = new Mutex(false, globalName);
            bool globalOwned;
            try
            {
                globalOwned = globalMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                globalOwned = true;
            }

            if (!globalOwned)
            {
                globalMutex.Dispose();
                return new PassLock(owned: false);
            }

            acquiredMutexes.Add(globalMutex);
            acquiredNames.Add(globalName);

            // Also lock all configured pairs if config file exists
            var pairNames = ReadPairNames(configPath);
            foreach (var p in pairNames)
            {
                var pMutexName = NameFor(configPath, p);
                var pMutex = new Mutex(false, pMutexName);
                bool pOwned;
                try
                {
                    pOwned = pMutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    pOwned = true;
                }

                if (!pOwned)
                {
                    pMutex.Dispose();
                    // Release all
                    foreach (var m in acquiredMutexes)
                    {
                        try { m.ReleaseMutex(); } catch { }
                        m.Dispose();
                    }
                    return new PassLock(owned: false);
                }

                acquiredMutexes.Add(pMutex);
                acquiredNames.Add(pMutexName);
            }

            foreach (var n in acquiredNames) HeldInProcess.Add(n);
            return new PassLock(acquiredNames, acquiredMutexes);
        }
    }

    private static PassLock TryAcquirePair(string configPath, string pairName, string globalName)
    {
        var pairMutexName = NameFor(configPath, pairName);

        lock (HeldInProcess)
        {
            if (HeldInProcess.Contains(globalName) || HeldInProcess.Contains(pairMutexName))
                return new PassLock(owned: false);

            // Cross-process check: verify global mutex is not held
            try
            {
                using var testGlobal = new Mutex(false, globalName);
                bool globalFree;
                try { globalFree = testGlobal.WaitOne(0); }
                catch (AbandonedMutexException) { globalFree = true; }
                if (!globalFree) return new PassLock(owned: false);
                testGlobal.ReleaseMutex();
            }
            catch { }

            // Acquire pair mutex
            var pairMutex = new Mutex(false, pairMutexName);
            bool pairOwned;
            try
            {
                pairOwned = pairMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                pairOwned = true;
            }

            if (!pairOwned)
            {
                pairMutex.Dispose();
                return new PassLock(owned: false);
            }

            // Verify again that global lock was not acquired concurrently
            try
            {
                using var testGlobal = new Mutex(false, globalName);
                bool globalFree;
                try { globalFree = testGlobal.WaitOne(0); }
                catch (AbandonedMutexException) { globalFree = true; }
                if (!globalFree)
                {
                    try { pairMutex.ReleaseMutex(); } catch { }
                    pairMutex.Dispose();
                    return new PassLock(owned: false);
                }
                testGlobal.ReleaseMutex();
            }
            catch { }

            HeldInProcess.Add(pairMutexName);
            return new PassLock(pairMutexName, pairMutex);
        }
    }

    private static List<string> ReadPairNames(string configPath)
    {
        var result = new List<string>();
        try
        {
            if (File.Exists(configPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("Pairs", out var pairs) ||
                    doc.RootElement.TryGetProperty("pairs", out pairs))
                {
                    if (pairs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var elem in pairs.EnumerateArray())
                        {
                            if (elem.TryGetProperty("Name", out var nameProp) ||
                                elem.TryGetProperty("name", out nameProp))
                            {
                                var name = nameProp.GetString();
                                if (!string.IsNullOrWhiteSpace(name))
                                    result.Add(name);
                            }
                        }
                    }
                }
            }
        }
        catch { }
        return result;
    }

    public void Dispose()
    {
        if (!_owned) return;
        lock (HeldInProcess)
        {
            foreach (var name in _names)
            {
                HeldInProcess.Remove(name);
            }
            foreach (var m in _mutexes)
            {
                try { m.ReleaseMutex(); } catch { }
                m.Dispose();
            }
            _mutexes.Clear();
            _names.Clear();
        }
    }
}
