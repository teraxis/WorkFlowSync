using System.Diagnostics;
using System.Runtime.Versioning;

namespace WorkFlowSync.Core;

/// <summary>
/// Two ways to run WorkFlowSync automatically without administrator rights
/// (docs/product/features/cli-and-scheduling.md):
///  - a shortcut in the user's Startup folder that starts the resident loop at logon;
///  - a Task Scheduler task (current user, "run only when logged on") that runs one pass every N minutes.
/// </summary>
public static class Autostart
{
    public const string ShortcutName = "WorkFlowSync.lnk";
    public const string TaskName = "WorkFlowSync";

    public static string StartupFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.Startup, Environment.SpecialFolderOption.Create);

    public static string ShortcutPath(string? startupFolder = null) => Path.Combine(startupFolder ?? StartupFolder, ShortcutName);

    /// <summary>wfs.exe next to the running executable (GUI and console are published side by side).</summary>
    public static string ConsoleExePath => Path.Combine(AppContext.BaseDirectory, "wfs.exe");

    // ---------------------------------------------------------------- Startup shortcut (resident loop)

    public static bool IsStartupShortcutEnabled(string? startupFolder = null) => File.Exists(ShortcutPath(startupFolder));

    /// <summary>Creates (or rewrites) the Startup shortcut: `wfs.exe sync --loop --config &lt;path&gt;`, minimised window.</summary>
    public static void EnableStartupShortcut(string configPath, string? startupFolder = null, string? exePath = null)
    {
        var lnk = ShortcutPath(startupFolder);
        var exe = exePath ?? ConsoleExePath;
        if (!File.Exists(exe)) throw new FileNotFoundException("Console executable not found next to the application.", exe);
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);

        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new PlatformNotSupportedException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic sc = shell.CreateShortcut(lnk);
        sc.TargetPath = exe;
        sc.Arguments = $"sync --loop --config \"{Path.GetFullPath(configPath)}\"";
        sc.WorkingDirectory = Path.GetDirectoryName(exe);
        sc.WindowStyle = 7;   // minimised
        sc.Description = "WorkFlowSync — фонова синхронізація мережевих папок";
        sc.Save();
    }

    public static void DisableStartupShortcut(string? startupFolder = null)
    {
        var lnk = ShortcutPath(startupFolder);
        if (File.Exists(lnk)) File.Delete(lnk);
    }

    // ---------------------------------------------------------------- Task Scheduler (one pass every N minutes)

    public static string BuildTaskCommand(string configPath, string? exePath = null) =>
        $"\"{exePath ?? ConsoleExePath}\" sync --once --config \"{Path.GetFullPath(configPath)}\"";

    /// <summary>Arguments for `schtasks` — no /RU and no /RL HIGHEST, so it runs as the current user, only when logged on.</summary>
    public static string BuildSchtasksCreateArgs(string configPath, int everyMinutes, string? exePath = null)
    {
        var tr = BuildTaskCommand(configPath, exePath).Replace("\"", "\\\"");
        return $"/Create /TN \"{TaskName}\" /TR \"{tr}\" /SC MINUTE /MO {Math.Clamp(everyMinutes, 1, 1439)} /F";
    }

    public static bool IsScheduledTaskEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    public static void EnableScheduledTask(string configPath, int everyMinutes, string? exePath = null)
    {
        var exe = exePath ?? ConsoleExePath;
        if (!File.Exists(exe)) throw new FileNotFoundException("Console executable not found next to the application.", exe);
        var rc = RunSchtasks(BuildSchtasksCreateArgs(configPath, everyMinutes, exe), out var output);
        if (rc != 0) throw new InvalidOperationException($"schtasks failed ({rc}): {output.Trim()}");
    }

    public static void DisableScheduledTask()
    {
        if (!IsScheduledTaskEnabled()) return;
        var rc = RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out var output);
        if (rc != 0) throw new InvalidOperationException($"schtasks failed ({rc}): {output.Trim()}");
    }

    private static int RunSchtasks(string args, out string output)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            output = stdout + stderr;
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }
}
