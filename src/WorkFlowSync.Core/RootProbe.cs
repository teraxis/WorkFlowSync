using System.Diagnostics;
using System.IO.Enumeration;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace WorkFlowSync.Core;

/// <summary>Where a pair root physically lives. Decides which change source may be used for it.</summary>
public enum RootKind
{
    /// <summary>Fixed local volume: change notifications are reliable.</summary>
    Local,

    /// <summary>Removable volume: notifications work while the medium is attached, and die with it.</summary>
    Removable,

    /// <summary>SMB share on the local network: notifications usually work, but stay a hint.</summary>
    LanShare,

    /// <summary>SMB share reached over the internet: high latency, notification support unproven.</summary>
    RemoteShare,
}

/// <summary>What a single probe of a root found. <see cref="Kind"/> is the part other code acts on.</summary>
public sealed record RootInfo
{
    public required string Path { get; init; }
    public required RootKind Kind { get; init; }

    /// <summary>True when the root can be listed right now.</summary>
    public bool Available { get; init; }

    /// <summary>UNC behind the path: the path itself, or what a mapped drive letter resolves to.</summary>
    public string? Unc { get; init; }

    public string? Host { get; init; }

    /// <summary>Resolved address of <see cref="Host"/>; null when it could not be resolved in time.</summary>
    public IPAddress? HostAddress { get; init; }

    /// <summary>True/false once the address is known; null when it is not.</summary>
    public bool? HostIsPrivate { get; init; }

    public DriveType DriveType { get; init; }

    /// <summary>Time to get the first entry of the root listing — roughly one round trip.</summary>
    public TimeSpan? FirstEntry { get; init; }

    /// <summary>Time to list the root directory (one level) and how many entries it holds.</summary>
    public TimeSpan? RootListing { get; init; }
    public int RootEntries { get; init; }

    /// <summary>Why the root could not be probed, when <see cref="Available"/> is false.</summary>
    public string? Problem { get; init; }
}

/// <summary>Result of listening for change notifications on a root (docs/plan-etap5.md §5.0).</summary>
public sealed class NotifyProbeResult
{
    /// <summary>False when the watcher could not even be created for this root.</summary>
    public bool Started { get; set; }
    public string? StartProblem { get; set; }

    public int Events { get; set; }
    public int Errors { get; set; }

    /// <summary>Time from start to the first event; null when nothing arrived.</summary>
    public TimeSpan? FirstEvent { get; set; }

    public TimeSpan Listened { get; set; }

    /// <summary>First few events, for the report. Bounded on purpose — a probe must not grow without limit.</summary>
    public List<string> Samples { get; } = new();

    /// <summary>
    /// No events is not proof that notifications do not work — nothing may have changed while we listened.
    /// </summary>
    public bool Inconclusive => Started && Events == 0;
}

/// <summary>
/// Classifies a pair root and measures what it costs to talk to it (docs/plan-etap5.md §4.1, §5.0).
/// Read-only: the probe never creates, renames or deletes anything inside the root.
/// </summary>
public static class RootProbe
{
    /// <summary>Below this a share behaves like the local network; above it, like the internet.</summary>
    public const double LanFirstEntryMs = 10.0;

    private const int MaxNotifySamples = 12;

    // Watching a whole tree is one kernel subscription; the buffer is what limits it (docs/plan-etap5.md §4.3).
    private const int WatcherBufferBytes = 64 * 1024;

    /// <summary>Shape of a path as text alone — no file system access, so it is cheap and unit-testable.</summary>
    public readonly record struct RootShape(bool IsUnc, string? Host, string? Share, char? DriveLetter);

    /// <summary>Splits a path into the parts that decide its kind. Never throws.</summary>
    public static RootShape Shape(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new RootShape(false, null, null, null);
        var p = path.Trim();

        // \\server\share\... and the extended forms \\?\UNC\server\share, \\.\...
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var rest = p[2..];
            if (rest.StartsWith(@"?\UNC\", StringComparison.OrdinalIgnoreCase)) rest = rest[6..];
            else if (rest.StartsWith(@"?\", StringComparison.Ordinal) || rest.StartsWith(@".\", StringComparison.Ordinal)) rest = rest[2..];

            var parts = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var host = parts.Length > 0 ? parts[0] : null;
            var share = parts.Length > 1 ? parts[1] : null;
            // \\?\C:\... is a local path written the long way, not a share.
            if (host is { Length: 2 } && host[1] == ':') return new RootShape(false, null, null, char.ToUpperInvariant(host[0]));
            return new RootShape(true, host, share, null);
        }

        if (p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0]))
            return new RootShape(false, null, null, char.ToUpperInvariant(p[0]));

        return new RootShape(false, null, null, null);
    }

    /// <summary>
    /// Kind from the path and the drive table alone — no listing, no DNS, safe to call from the UI thread.
    /// Returns null for a network share: telling a share on the local network from one on the internet needs
    /// <see cref="Probe"/>. An empty or unusable path also returns null, so check that first when it matters.
    /// </summary>
    public static RootKind? QuickKind(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var shape = Shape(path);
        if (shape.IsUnc) return null;
        if (shape.DriveLetter is not { } letter) return null;

        try
        {
            return new DriveInfo($"{letter}:\\").DriveType switch
            {
                DriveType.Network => null,
                DriveType.Removable or DriveType.CDRom => RootKind.Removable,
                DriveType.Fixed or DriveType.Ram => RootKind.Local,
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>RFC 1918 / loopback / link-local / CGNAT / unique-local: an address that cannot be the open internet.</summary>
    public static bool IsPrivateAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                10 => true,
                127 => true,
                169 when b[1] == 254 => true,                 // link-local
                172 when b[1] >= 16 && b[1] <= 31 => true,
                192 when b[1] == 168 => true,
                100 when b[1] >= 64 && b[1] <= 127 => true,    // CGNAT, RFC 6598
                _ => false,
            };
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            if (ip.IsIPv4MappedToIPv6) return IsPrivateAddress(ip.MapToIPv4());
            var b = ip.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC;                     // fc00::/7 unique local
        }

        return false;
    }

    /// <summary>
    /// The decision itself, kept free of I/O so every row can be unit-tested.
    /// A known address wins; otherwise latency decides, and without either we assume the worse (remote).
    /// </summary>
    public static RootKind Classify(DriveType driveType, bool isUnc, bool? hostIsPrivate, double? firstEntryMs)
    {
        if (isUnc || driveType == DriveType.Network)
        {
            if (hostIsPrivate is true) return RootKind.LanShare;
            if (hostIsPrivate is false) return RootKind.RemoteShare;
            return firstEntryMs is { } ms && ms < LanFirstEntryMs ? RootKind.LanShare : RootKind.RemoteShare;
        }

        return driveType switch
        {
            DriveType.Removable or DriveType.CDRom => RootKind.Removable,
            _ => RootKind.Local,
        };
    }

    /// <summary>Classifies the root and measures listing latency. Read-only; never throws for an unavailable root.</summary>
    public static RootInfo Probe(string path, CancellationToken ct = default)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch (Exception ex) { return new RootInfo { Path = path, Kind = RootKind.Local, Available = false, Problem = ex.Message }; }
        if (full.Length == 2 && full[1] == ':') full += '\\';

        var shape = Shape(full);
        var driveType = DriveType.Unknown;
        string? unc = shape.IsUnc ? full : null;

        if (shape.DriveLetter is { } letter)
        {
            try { driveType = new DriveInfo($"{letter}:\\").DriveType; }
            catch (ArgumentException) { /* not a real drive; stays Unknown */ }
            if (driveType == DriveType.Network) unc = ResolveMappedDrive(letter);
        }

        // The host comes from whichever UNC we ended up with: the path itself, or what the letter maps to.
        var host = shape.IsUnc ? shape.Host : (unc is null ? null : Shape(unc).Host);
        var address = host is null ? null : ResolveHost(host, TimeSpan.FromSeconds(2));

        TimeSpan? firstEntry = null, rootListing = null;
        var entries = 0;
        string? problem = null;
        var available = false;

        if (!Directory.Exists(full))
        {
            problem = "root is not available (offline, not mapped, or does not exist)";
        }
        else
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
            try
            {
                var sw = Stopwatch.StartNew();
                using (var e = new FileSystemEnumerable<int>(full, (ref FileSystemEntry _) => 0, options).GetEnumerator())
                    e.MoveNext();
                firstEntry = sw.Elapsed;

                sw.Restart();
                foreach (var _ in new FileSystemEnumerable<int>(full, (ref FileSystemEntry _) => 0, options))
                {
                    entries++;
                    ct.ThrowIfCancellationRequested();
                }
                rootListing = sw.Elapsed;
                available = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        return new RootInfo
        {
            Path = full,
            Kind = Classify(driveType, shape.IsUnc, address is null ? null : IsPrivateAddress(address), firstEntry?.TotalMilliseconds),
            Available = available,
            Unc = unc,
            Host = host,
            HostAddress = address,
            HostIsPrivate = address is null ? null : IsPrivateAddress(address),
            DriveType = driveType,
            FirstEntry = firstEntry,
            RootListing = rootListing,
            RootEntries = entries,
            Problem = problem,
        };
    }

    /// <summary>
    /// Listens for change notifications without touching the root. Returns when <paramref name="duration"/>
    /// elapses or the token is cancelled. Handlers only count and sample — see docs/plan-etap5.md §4.3, rule 1.
    /// </summary>
    public static NotifyProbeResult WatchNotify(string root, TimeSpan duration, Action<string>? onEvent = null, CancellationToken ct = default)
    {
        var result = new NotifyProbeResult();
        var sw = Stopwatch.StartNew();
        var gate = new object();

        void Record(string line)
        {
            lock (gate)
            {
                result.Events++;
                result.FirstEvent ??= sw.Elapsed;
                if (result.Samples.Count < MaxNotifySamples) result.Samples.Add(line);
            }
            onEvent?.Invoke(line);
        }

        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = WatcherBufferBytes,
                // Attributes are left out on purpose: OneDrive hydration would look like a change (§4.3, rule 5).
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
        }
        catch (Exception ex)
        {
            result.StartProblem = $"{ex.GetType().Name}: {ex.Message}";
            return result;
        }

        using (watcher)
        {
            watcher.Created += (_, e) => Record($"created  {e.Name}");
            watcher.Deleted += (_, e) => Record($"deleted  {e.Name}");
            watcher.Changed += (_, e) => Record($"changed  {e.Name}");
            watcher.Renamed += (_, e) => Record($"renamed  {e.OldName} -> {e.Name}");
            watcher.Error += (_, e) =>
            {
                lock (gate) result.Errors++;
                onEvent?.Invoke($"ERROR    {e.GetException().Message}");
            };

            try
            {
                watcher.EnableRaisingEvents = true;
                result.Started = true;
            }
            catch (Exception ex)
            {
                result.StartProblem = $"{ex.GetType().Name}: {ex.Message}";
                return result;
            }

            ct.WaitHandle.WaitOne(duration);
            watcher.EnableRaisingEvents = false;
        }

        result.Listened = sw.Elapsed;
        return result;
    }

    // ------------------------------------------------------------------ mapped drives

    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetGetConnectionW")]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    /// <summary>UNC behind a mapped drive letter, or null when the letter is not a network mapping.</summary>
    public static string? ResolveMappedDrive(char letter)
    {
        var local = $"{char.ToUpperInvariant(letter)}:";
        var length = 260;
        var buffer = new StringBuilder(length);
        var rc = WNetGetConnection(local, buffer, ref length);
        if (rc == ErrorMoreData)
        {
            buffer = new StringBuilder(length);
            rc = WNetGetConnection(local, buffer, ref length);
        }
        return rc == ErrorSuccess ? buffer.ToString().TrimEnd('\\') : null;
    }

    private static IPAddress? ResolveHost(string host, TimeSpan timeout)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;
        try
        {
            var task = Dns.GetHostAddressesAsync(host);
            return task.Wait(timeout) && task.Result.Length > 0 ? task.Result[0] : null;
        }
        catch
        {
            return null;   // no name resolution is not an error for us; latency then decides the kind
        }
    }
}
