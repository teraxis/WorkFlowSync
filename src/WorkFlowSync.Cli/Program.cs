using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.Cli;

/// <summary>
/// Entry point. Command surface is specified in docs/product/features/cli-and-scheduling.md.
/// Exit codes: 0 ok, 1 runtime error, 2 usage/config error, 3 not implemented yet.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

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
                "sync" => NotImplemented("sync"),
                "status" => NotImplemented("status"),
                "import-excludes" => NotImplemented("import-excludes"),
                _ => Usage(2),
            };
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

    private static int ConfigCommand(Options opts)
    {
        var path = opts.ConfigPath;
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"config not found: {path}");
            return 2;
        }

        var cfg = SyncConfig.Load(path);
        var problems = cfg.Validate();
        Console.WriteLine($"config: {Path.GetFullPath(path)}");
        foreach (var pair in cfg.Pairs)
        {
            var retention = pair.Retention is { } r ? $"{r.TotalDays:0} days" : "forever";
            Console.WriteLine($"  [{pair.Name}] {pair.Source} -> {pair.Target}  links={pair.Links}  retention={retention}  exclude={pair.Exclude.Count}");
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
              wfs sync [--once | --loop] [--config <path>] [--dry-run]
              wfs status [--config <path>]
              wfs config validate [--config <path>]
              wfs import-excludes <batch.ffs_batch> [--config <path>]
              wfs version

            options:
              --config <path>   config file (default: config.json next to the executable)
              --once            run one pass and exit (Task Scheduler mode)
              --loop            stay resident and repeat every configured interval
              --dry-run         report planned actions without touching the file system
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
                default: o.Positional.Add(list[i]); break;
            }
        }
        return o;
    }

    private sealed class Options
    {
        public string ConfigPath { get; set; } = WorkFlowSync.Core.Config.ConfigFile.DefaultPath;
        public bool Once { get; set; }
        public bool Loop { get; set; }
        public bool DryRun { get; set; }
        public List<string> Positional { get; } = new();
    }
}
