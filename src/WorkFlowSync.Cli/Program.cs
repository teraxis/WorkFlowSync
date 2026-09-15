using System.Text;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
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
            var retention = pair.Retention is { } r ? $"{r.TotalDays:0} days" : "forever";
            Console.WriteLine($"  [{pair.Name}]{(pair.Enabled ? "" : " (paused)")} {pair.Source} -> {pair.Target}  links={pair.Links}  retention={retention}  exclude={pair.Exclude.Count}");
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

        var logDir = Path.IsPathRooted(cfg.LogPath) ? cfg.LogPath : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(opts.ConfigPath))!, cfg.LogPath);
        using var log = new FileSyncLog(logDir, (level, line) =>
        {
            var w = level >= LogLevel.Warn ? Console.Error : Console.Out;
            w.WriteLine(line);
        }, opts.Verbose);

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
        var pass = new SyncRunner(cfg, opts.ConfigPath, log).Run(opts.DryRun, opts.Backfill, cts.Token, opts.PairName);
        return pass.Errors == 0 ? 0 : 1;
    }

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
        Console.WriteLine($"state: {runner.StatePath}  {Core.Execution.SyncExecutor.FormatSize(store.FileSizeBytes)}  entries={total:N0}");
        Console.WriteLine($"last pass: {store.GetMeta("last_run") ?? "-"}  result={store.GetMeta("last_run_result") ?? "-"}");
        foreach (var pair in cfg.Pairs)
        {
            var c = store.CountByStatus(pair.Name);
            int Get(EntryStatus s) => c.TryGetValue(s, out var n) ? n : 0;
            Console.WriteLine($"[{pair.Name}]  active={Get(EntryStatus.Active):N0}  tombstone={Get(EntryStatus.Tombstone):N0}  local_modified={Get(EntryStatus.LocalModified):N0}");
        }
        return 0;
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
              wfs sync [--once | --loop] [--pair <name>] [--config <path>] [--dry-run] [--backfill] [--verbose]
              wfs status [--config <path>]
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
        public string? PairName { get; set; }
        public List<string> Positional { get; } = new();
    }
}
