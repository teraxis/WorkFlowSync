using System.Diagnostics;
using System.Text;

namespace WorkFlowSync.Core.Update;

/// <summary>
/// Replaces the running executable with a downloaded update.
///
/// A single-file .NET app cannot overwrite itself while running. The strategy:
/// 1. The new exe is downloaded as <c>WorkFlowSync.update.exe</c> next to the running one.
/// 2. A temporary batch script is created that waits for this process to exit, swaps the files,
///    starts the new exe, and cleans up.
/// 3. This process launches the script and exits.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>
    /// Prepares and launches the replacement script.  The caller should exit the application
    /// promptly after this method returns (the script waits up to 10 seconds for the process to end).
    /// </summary>
    /// <param name="currentExePath">Full path to the running WorkFlowSync.exe.</param>
    /// <param name="updateExePath">Full path to the downloaded WorkFlowSync.update.exe.</param>
    /// <param name="restartArgs">Optional arguments to pass when restarting (e.g. "--tray").</param>
    public static void LaunchAndExit(string currentExePath, string updateExePath, string? restartArgs = null)
    {
        var dir = Path.GetDirectoryName(currentExePath)!;
        var oldName = Path.GetFileNameWithoutExtension(currentExePath) + ".old" + Path.GetExtension(currentExePath);
        var oldPath = Path.Combine(dir, oldName);
        var scriptPath = Path.Combine(dir, "wfs-update.cmd");
        var pid = Environment.ProcessId;

        // The script:
        //  - Uses tasklist to poll until the current process exits (up to 30 seconds)
        //  - Moves the running exe to .old  (rename is allowed even while the file is mapped)
        //  - Moves the update exe into position
        //  - Starts the new exe
        //  - Deletes the .old file and itself
        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("chcp 65001 >nul 2>&1");
        script.AppendLine($"echo Waiting for WorkFlowSync (PID {pid}) to exit...");
        script.AppendLine($"set /a tries=0");
        script.AppendLine(":wait");
        script.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL");
        script.AppendLine("if errorlevel 1 goto :proceed");
        script.AppendLine("set /a tries+=1");
        script.AppendLine("if %tries% GEQ 30 (echo Timeout waiting for process exit. & exit /b 1)");
        script.AppendLine("timeout /t 1 /nobreak >nul");
        script.AppendLine("goto :wait");
        script.AppendLine(":proceed");
        script.AppendLine($"if exist \"{oldPath}\" del /f /q \"{oldPath}\"");
        script.AppendLine($"move /y \"{currentExePath}\" \"{oldPath}\"");
        script.AppendLine($"move /y \"{updateExePath}\" \"{currentExePath}\"");
        script.AppendLine($"start \"\" \"{currentExePath}\" {restartArgs ?? ""}");
        script.AppendLine("timeout /t 2 /nobreak >nul");
        script.AppendLine($"if exist \"{oldPath}\" del /f /q \"{oldPath}\"");
        // Self-delete: "del" of the running batch is allowed on Windows because cmd.exe
        // reads the file line by line.
        script.AppendLine($"del /f /q \"{scriptPath}\"");

        File.WriteAllText(scriptPath, script.ToString(), new UTF8Encoding(false));

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process.Start(psi);
    }

    /// <summary>
    /// Returns the path where the downloaded update exe should be saved.
    /// </summary>
    public static string UpdateExePath(string currentExePath)
    {
        var dir = Path.GetDirectoryName(currentExePath)!;
        var name = Path.GetFileNameWithoutExtension(currentExePath) + ".update" + Path.GetExtension(currentExePath);
        return Path.Combine(dir, name);
    }

    /// <summary>
    /// Cleans up leftover files from a previous update (called on startup).
    /// </summary>
    public static void CleanupPreviousUpdate(string currentExePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(currentExePath)!;
            var oldPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(currentExePath) + ".old" + Path.GetExtension(currentExePath));
            var scriptPath = Path.Combine(dir, "wfs-update.cmd");
            if (File.Exists(oldPath)) File.Delete(oldPath);
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
        catch
        {
            // Best effort: a locked file will be removed next time.
        }
    }
}
