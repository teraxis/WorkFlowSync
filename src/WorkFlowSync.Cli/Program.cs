using System.Text;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Cli;

/// <summary>
/// Entry point of wfs.exe. Command surface is specified in docs/product/features/cli-and-scheduling.md.
/// Exit codes: 0 ok, 1 runtime error, 2 usage/config error, 3 not implemented yet, 4 another instance is running.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var code = Run(args);

        // Started by double-click (we own the console alone): keep the window open so the output is readable.
        if (ConsoleOwner.IsSoleOwner())
        {
            Console.WriteLine();
            Console.Write("Press any key to close...");
            Console.ReadKey(intercept: true);
        }
        return code;
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            return Usage(0);

        var opts = ParseOptions(args.Skip(1));
        try
        {
            return args[0] switch
            {
                "version" => Version(),
                "config" => ConfigCommand(opts),
                "sync" => SyncCommand(opts),
                "status" => StatusCommand(opts),
                "pending" => PendingCommand(opts),
                "probe" => ProbeCommand(opts),
                "forget" => ForgetCommand(opts),
                "autostart" => AutostartCommand(opts),
                "task" => TaskCommand(opts),
                _ => Usage(2),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("interrupted");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Version()
    {
        Console.WriteLine($"{WorkFlowSyncInfo.Name} {WorkFlowSyncInfo.Version}");
        return 0;
    }

    private static SyncConfig? LoadConfig(Options opts, out int exitCode)
    {
        exitCode = 0;
        if (!File.Exists(opts.ConfigPath))
        {
            Console.Error.WriteLine($"config not found: {opts.ConfigPath}");
            exitCode = 2;
            return null;
        }
        var cfg = SyncConfig.Load(opts.ConfigPath);
        var problems = cfg.Validate();
        if (problems.Count > 0)
        {
            foreach (var p in problems) Console.Error.WriteLine($"  ! {p}");
            exitCode = 2;
            return null;
        }
        return cfg;
    }

    private static int ConfigCommand(Options opts)
    {
        if (!File.Exists(opts.ConfigPath))
        {
            Console.Error.WriteLine($"config not found: {opts.ConfigPath}");
            return 2;
        }

        var cfg = SyncConfig.Load(opts.ConfigPath);
        var problems = cfg.Validate();
        Console.WriteLine($"config: {Path.GetFullPath(opts.ConfigPath)}");
        foreach (var pair in cfg.Pairs)
        {
            var maxAge = pair.MaxAge is { } ma ? $"{ma.TotalDays:0} days" : "any age";
            var autoClean = pair.AutoClean is { } ac ? $"{ac.TotalDays:0} days" : "off";
            var every = pair.Interval is { } pi ? $"{pi.TotalMinutes:0} min" : "as configured";
            Console.WriteLine($"  [{pair.Name}]{(pair.Enabled ? "" : " (paused)")} {pair.Source} -> {pair.Target}  every={every}  links={pair.Links}  watch={pair.Watch}  max_age={maxAge}  auto_clean={autoClean}  exclude={pair.Exclude.Count}");
        }
        Console.WriteLine($"  interval={cfg.Interval}  state={cfg.StatePath}  logs={cfg.LogPath}  parallelism={cfg.ScanParallelism}  buffer={cfg.ScanBufferSize}");

        if (problems.Count == 0)
        {
            Console.WriteLine("config: OK");
            return 0;
        }
        foreach (var p in problems) Console.Error.WriteLine($"  ! {p}");
        return 2;
    }

    private static int SyncCommand(Options opts)
    {
        var cfg = LoadConfig(opts, out var code);
        if (cfg is null) return code;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // wfs.exe is always the background worker (Startup loop, Task Scheduler), so the whole
        // process yields to whatever the user is doing (docs F5: «Навантаження на процесор»).
        CpuBudget.ApplyToCurrentProcess(cfg.CpuLoad);

        var logDir = Path.IsPathRooted(cfg.LogPath) ? cfg.LogPath : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(opts.ConfigPath))!, cfg.LogPath);
        using var log = new FileSyncLog(logDir, (level, line) =>
        {
            var w = level >= LogLevel.Warn ? Console.Error : Console.Out;
            w.WriteLine(line);
        }, opts.Verbose, cfg.LogKeepDays);

        if (opts.Loop && opts.Scope is not null)
        {
            Console.Error.WriteLine("--scope is for a single pass; it cannot be combined with --loop");
            return 2;
        }

        if (opts.Loop)
        {
            // Resident mode: the loop itself takes the pass lock per pass, so a scheduled --once can interleave safely.
            var loop = new LoopRunner(opts.ConfigPath, log, opts.DryRun);
            loop.RunAsync(cts.Token).GetAwaiter().GetResult();
            return 0;
        }

        using var gate = PassLock.TryAcquire(opts.ConfigPath);
        if (!gate.Acquired)
        {
            Console.Error.WriteLine("another WorkFlowSync pass is running for this config");
            return 4;
        }
        var pass = new SyncRunner(cfg, opts.ConfigPath, log).Run(opts.DryRun, opts.Backfill, cts.Token, opts.PairName, opts.Scope);
        return pass.Errors == 0 ? 0 : 1;
    }

    /// <summary>
    /// Diagnostics for one root: what kind of storage it is, what a listing costs, and (with --watch)
    /// whether change notifications actually arrive. Read-only — nothing inside the root is touched.
    /// </summary>
    private static int ProbeCommand(Options opts)
    {
        var target = opts.Positional.FirstOrDefault();
        if (target is null)
        {
            Console.Error.WriteLine("usage: wfs probe <path> [--watch <seconds>]");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var info = RootProbe.Probe(target, cts.Token);
        static string Ms(TimeSpan? t) => t is { } v ? $"{v.TotalMilliseconds:N1} ms" : "-";

        Console.WriteLine($"root: {info.Path}");
        Console.WriteLine($"  kind:         {info.Kind}");
        Console.WriteLine($"  drive type:   {info.DriveType}");
        if (info.Unc is not null) Console.WriteLine($"  unc:          {info.Unc}");
        if (info.Host is not null)
        {
            var addr = info.HostAddress is null ? "not resolved" : $"{info.HostAddress} ({(info.HostIsPrivate == true ? "private" : "public")})";
            Console.WriteLine($"  host:         {info.Host} -> {addr}");
        }
        Console.WriteLine($"  available:    {(info.Available ? "yes" : "no")}{(info.Problem is null ? "" : $"  ({info.Problem})")}");
        Console.WriteLine($"  first entry:  {Ms(info.FirstEntry)}   (roughly one round trip)");
        Console.WriteLine($"  root listing: {Ms(info.RootListing)} for {info.RootEntries} entries");
        Console.WriteLine($"  change source: {SuggestedSource(info.Kind)}");

        if (!info.Available) return 1;
        if (opts.WatchSeconds is not { } seconds) return 0;

        Console.WriteLine();
        Console.WriteLine($"listening for change notifications for {seconds} s (read-only).");
        Console.WriteLine("change a file in this folder - from here or from another machine - to prove they arrive.");
        var notify = RootProbe.WatchNotify(info.Path, TimeSpan.FromSeconds(seconds),
            line => Console.WriteLine($"  {line}"), cts.Token);

        Console.WriteLine();
        if (!notify.Started)
        {
            Console.WriteLine($"notify: NOT SUPPORTED for this root ({notify.StartProblem})");
            return 0;
        }
        Console.WriteLine($"notify: events={notify.Events} errors={notify.Errors} first={(notify.FirstEvent is { } f ? $"{f.TotalSeconds:N1} s" : "-")} listened={notify.Listened.TotalSeconds:N0} s");
        Console.WriteLine(notify.Inconclusive
            ? "notify: no events seen - inconclusive, nothing may have changed while listening."
            : "notify: notifications arrive for this root.");
        return 0;
    }

    private static string SuggestedSource(RootKind kind) => kind switch
    {
        RootKind.Local or RootKind.Removable => "LocalWatcher + IntervalScan (changes within seconds)",
        RootKind.LanShare => "SmbWatcher + IntervalScan (watcher is a hint, full pass stays the truth)",
        _ => "IntervalScan (run with --watch to find out whether notifications work at all)",
    };

    private static int AutostartCommand(Options opts)
    {
        var verb = opts.Positional.FirstOrDefault() ?? "status";
        switch (verb)
        {
            case "on":
                if (LoadConfig(opts, out var c1) is null) return c1;
                var mode = opts.Tray ? Autostart.Mode.Tray : Autostart.Mode.ConsoleLoop;
                Autostart.EnableStartupShortcut(opts.ConfigPath, mode: mode);
                Console.WriteLine($"autostart: enabled  mode={mode}  ({Autostart.ShortcutPath()})");
                return 0;
            case "off":
                Autostart.DisableStartupShortcut();
                Console.WriteLine("autostart: disabled");
                return 0;
            case "status":
                var current = Autostart.ReadStartupShortcutMode();
                Console.WriteLine($"autostart: {(current is null ? "disabled" : $"enabled  mode={current}")}  ({Autostart.ShortcutPath()})");
                return 0;
            default:
                Console.Error.WriteLine("usage: wfs autostart on|off|status [--config <path>]");
                return 2;
        }
    }

    private static int TaskCommand(Options opts)
    {
        var verb = opts.Positional.FirstOrDefault() ?? "status";
        switch (verb)
        {
            case "on":
                var cfg = LoadConfig(opts, out var c1);
                if (cfg is null) return c1;
                Autostart.EnableScheduledTask(opts.ConfigPath, (int)Math.Round(cfg.Interval.TotalMinutes));
                Console.WriteLine($"task: enabled  ({Autostart.TaskName}, every {cfg.Interval.TotalMinutes:0} min, current user, only when logged on)");
                return 0;
            case "off":
                Autostart.DisableScheduledTask();
                Console.WriteLine("task: disabled");
                return 0;
            case "status":
                Console.WriteLine($"task: {(Autostart.IsScheduledTaskEnabled() ? "enabled" : "disabled")}  ({Autostart.TaskName})");
                return 0;
            default:
                Console.Error.WriteLine("usage: wfs task on|off|status [--config <path>]");
                return 2;
        }
    }

    private static int StatusCommand(Options opts)
    {
        var cfg = LoadConfig(opts, out var code);
        if (cfg is null) return code;

        var runner = new SyncRunner(cfg, opts.ConfigPath, new MemorySyncLog());
        Console.WriteLine($"{WorkFlowSyncInfo.Name} {WorkFlowSyncInfo.Version}   config: {Path.GetFullPath(opts.ConfigPath)}");
        if (!File.Exists(runner.StatePath))
        {
            Console.WriteLine($"state: {runner.StatePath}  (no passes yet)");
            return 0;
        }
        using var store = new StateStore(runner.StatePath);
        var total = cfg.Pairs.Sum(p => store.Count(p.Name));
        var pendingCount = store.Pending.CountOpen();
        Console.WriteLine($"state: {runner.StatePath}  {Core.Execution.SyncExecutor.FormatSize(store.FileSizeBytes)}  entries={total:N0}  pending={pendingCount}");
        Console.WriteLine($"last pass: {store.GetMeta("last_run") ?? "-"}  result={store.GetMeta("last_run_result") ?? "-"}");
        foreach (var pair in cfg.Pairs)
        {
            var c = store.CountByStatus(pair.Name);
            int Get(EntryStatus s) => c.TryGetValue(s, out var n) ? n : 0;
            var pairPending = store.Pending.CountOpen(pair.Name);
            Console.WriteLine($"[{pair.Name}]  active={Get(EntryStatus.Active):N0}  tombstone={Get(EntryStatus.Tombstone):N0}  diverged={Get(EntryStatus.Diverged):N0}  pending={pairPending}");
        }
        return pendingCount > 0 ? 5 : 0;
    }

    private static int PendingCommand(Options opts)
    {
        var verb = opts.Positional.FirstOrDefault() ?? "list";
        var cfg = LoadConfig(opts, out var code);
        if (cfg is null) return code;

        var runner = new SyncRunner(cfg, opts.ConfigPath, new MemorySyncLog());
        if (!File.Exists(runner.StatePath))
        {
            if (verb == "list")
            {
                if (opts.Json) Console.WriteLine("[]");
                else Console.WriteLine("open pending entries: 0");
                return 0;
            }
            Console.Error.WriteLine("state database does not exist yet");
            return 1;
        }

        using var store = new StateStore(runner.StatePath);

        if (verb == "list")
        {
            var open = store.Pending.GetOpen(opts.PairName);
            if (opts.Json)
            {
                var list = open.Select(e => new
                {
                    id = e.Id,
                    pair = e.Pair,
                    path = e.Path,
                    from_path = e.FromPath,
                    change = e.Change.ToString().ToLowerInvariant(),
                    kind = e.Kind.ToString().ToLowerInvariant(),
                    detected = e.DetectedUtc.ToString("O")
                });
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(list, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine($"open pending entries: {open.Count}");
                foreach (var e in open)
                {
                    var from = e.FromPath is not null ? $" (from: {e.FromPath})" : "";
                    Console.WriteLine($"  #{e.Id} [{e.Pair}] {e.Change}: {e.Path}{from}");
                }
            }
            return 0;
        }

        if (verb is "approve" or "dismiss" or "revert")
        {
            if (!opts.All && !opts.Id.HasValue && string.IsNullOrEmpty(opts.PathArg))
            {
                Console.Error.WriteLine($"usage: wfs pending {verb} (--id <n> | --path <path> | --all) [--pair <name>] [--yes]");
                return 2;
            }

            if (opts.All && opts.PairName is null && !opts.Yes)
            {
                Console.Error.WriteLine("error: --all across all pairs requires --yes");
                return 2;
            }

            var service = new ApprovalService(store, new ConsoleSyncLog());
            var now = DateTimeOffset.UtcNow;
            var pairRoots = cfg.Pairs.ToDictionary(p => p.Name, p => (p.Source, p.Target), StringComparer.OrdinalIgnoreCase);

            List<PendingEntry> targets = new();
            if (opts.Id.HasValue)
            {
                var entry = store.Pending.GetById(opts.Id.Value);
                if (entry is null || entry.Status != PendingStatus.Pending)
                {
                    Console.Error.WriteLine($"pending entry #{opts.Id.Value} not found or no longer open.");
                    return 1;
                }
                targets.Add(entry);
            }
            else if (!string.IsNullOrEmpty(opts.PathArg))
            {
                if (opts.PairName is null)
                {
                    Console.Error.WriteLine("--path requires --pair <name>");
                    return 2;
                }
                var entry = store.Pending.GetOpenByPath(opts.PairName, opts.PathArg);
                if (entry is null)
                {
                    Console.Error.WriteLine($"pending entry for '{opts.PathArg}' in pair [{opts.PairName}] not found.");
                    return 1;
                }
                targets.Add(entry);
            }
            else if (opts.All)
            {
                targets.AddRange(store.Pending.GetOpen(opts.PairName));
            }

            var success = true;
            foreach (var item in targets)
            {
                if (!pairRoots.TryGetValue(item.Pair, out var roots))
                {
                    Console.Error.WriteLine($"[{item.Pair}] pair configuration not found");
                    success = false;
                    continue;
                }

                var res = verb switch
                {
                    "approve" => service.ApproveAsync(item.Id, roots.Source, roots.Target, now).GetAwaiter().GetResult(),
                    "dismiss" => service.DismissAsync(item.Id, roots.Source, roots.Target, now).GetAwaiter().GetResult(),
                    "revert" => service.RevertAsync(item.Id, roots.Source, roots.Target, now).GetAwaiter().GetResult(),
                    _ => new ApprovalResult()
                };

                if (!res.IsSuccess)
                {
                    success = false;
                    foreach (var err in res.Errors) Console.Error.WriteLine($"  ! {err}");
                }
                else
                {
                    Console.WriteLine($"#{item.Id} [{item.Pair}] {verb}: {item.Path}");
                }
            }
            return success ? 0 : 1;
        }

        Console.Error.WriteLine("usage: wfs pending list|approve|dismiss|revert ...");
        return 2;
    }

    private sealed class ConsoleSyncLog : ISyncLog
    {
        public void Write(LogLevel level, string message) => Console.WriteLine($"[{level}] {message}");
    }

    private static int ForgetCommand(Options opts)
    {
        if (opts.Positional.Count == 0)
        {
            Console.Error.WriteLine("usage: wfs forget <relative-path> [--pair <name>] [--config <path>]");
            return 2;
        }
        var cfg = LoadConfig(opts, out var code);
        if (cfg is null) return code;
        var runner = new SyncRunner(cfg, opts.ConfigPath, new MemorySyncLog());
        if (!File.Exists(runner.StatePath)) { Console.WriteLine("no state yet"); return 0; }
        using var store = new StateStore(runner.StatePath);
        var pairs = opts.PairName is { } n ? cfg.Pairs.Where(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).ToList() : cfg.Pairs;
        if (pairs.Count == 0) { Console.Error.WriteLine($"unknown pair: {opts.PairName}"); return 2; }
        var total = 0;
        foreach (var pair in pairs)
        {
            var n2 = store.DeleteSubtree(pair.Name, opts.Positional[0]);
            Console.WriteLine($"[{pair.Name}] forgot {n2:N0} row(s) under \\{opts.Positional[0].Trim('\\')}");
            total += n2;
        }
        Console.WriteLine(total == 0 ? "nothing matched" : "the item(s) will be treated as new on the next pass");
        return 0;
    }

    private static int NotImplemented(string command)
    {
        Console.Error.WriteLine($"'{command}' is not implemented yet (see docs/requirements.md, stage plan).");
        return 3;
    }

    private static int Usage(int code)
    {
        var w = code == 0 ? Console.Out : Console.Error;
        w.WriteLine($"""
            {WorkFlowSyncInfo.Name} {WorkFlowSyncInfo.Version} (wfs = console) - one-way mirror with memory (network share -> local/OneDrive)

            usage:
              wfs sync [--once | --loop] [--pair <name>] [--scope <rel>] [--config <path>] [--dry-run] [--backfill] [--verbose]
              wfs status [--config <path>]
              wfs probe <path> [--watch <seconds>]            what kind of storage a root is, what a listing costs,
                                                              and whether change notifications arrive (read-only)
              wfs config validate [--config <path>]
              wfs forget <relative-path> [--pair <name>] [--config <path>]
              wfs autostart on|off|status [--tray] [--config <path>]   Startup-folder shortcut at logon
                                                              (default: wfs sync --loop; --tray: the app in the notification area)
              wfs task on|off|status [--config <path>]        Task Scheduler task running `sync --once` every <interval>
              wfs version

            options:
              --config <path>   config file (default: config.json next to the executable)
              --once            run one pass and exit (default; Task Scheduler mode)
              --loop            stay resident and repeat every configured interval (config is re-read each pass)
              --dry-run         report planned actions without touching the file system or the state
              --backfill        take first_seen from min(ctime, mtime) even when state already exists
              --verbose         per-directory DEBUG lines in the log
              --pair <name>     act on one pair only (sync / forget); a paused pair still runs
              --scope <rel>     sync only this folder inside the pair (relative to its root): additions and
                                updates only, never deletions. Needs one pair; not valid with --loop
              --watch <sec>     probe: listen for change notifications for this many seconds

            exit codes: 0 ok, 1 error, 2 usage/config, 3 not implemented, 4 another pass is running
            """);
        return code;
    }

    private static Options ParseOptions(IEnumerable<string> args)
    {
        var o = new Options();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            switch (list[i])
            {
                case "--config" when i + 1 < list.Count:
                    o.ConfigPath = list[++i];
                    break;
                case "--once": o.Once = true; break;
                case "--loop": o.Loop = true; break;
                case "--dry-run": o.DryRun = true; break;
                case "--backfill": o.Backfill = true; break;
                case "--verbose": o.Verbose = true; break;
                case "--tray": o.Tray = true; break;
                case "--pair" when i + 1 < list.Count:
                    o.PairName = list[++i];
                    break;
                case "--watch" when i + 1 < list.Count && int.TryParse(list[i + 1], out var seconds):
                    o.WatchSeconds = Math.Clamp(seconds, 1, 3600);
                    i++;
                    break;
                case "--scope" when i + 1 < list.Count:
                    o.Scope = list[++i];
                    break;
                case "--json": o.Json = true; break;
                case "--all": o.All = true; break;
                case "--yes": o.Yes = true; break;
                case "--id" when i + 1 < list.Count && long.TryParse(list[i + 1], out var idVal):
                    o.Id = idVal;
                    i++;
                    break;
                case "--path" when i + 1 < list.Count:
                    o.PathArg = list[++i];
                    break;
                default: o.Positional.Add(list[i]); break;
            }
        }
        return o;
    }

    private sealed class Options
    {
        public string ConfigPath { get; set; } = ConfigFile.DefaultPath;
        public bool Once { get; set; }
        public bool Loop { get; set; }
        public bool DryRun { get; set; }
        public bool Backfill { get; set; }
        public bool Verbose { get; set; }
        public bool Tray { get; set; }
        public bool Json { get; set; }
        public bool All { get; set; }
        public bool Yes { get; set; }
        public long? Id { get; set; }
        public string? PathArg { get; set; }
        public string? PairName { get; set; }
        public int? WatchSeconds { get; set; }
        public string? Scope { get; set; }
        public List<string> Positional { get; } = new();
    }
}
