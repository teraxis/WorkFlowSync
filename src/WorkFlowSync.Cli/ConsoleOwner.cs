using System.Runtime.InteropServices;

namespace WorkFlowSync.Cli;

/// <summary>Detects whether this process was launched with its own fresh console (Explorer double-click).</summary>
internal static class ConsoleOwner
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    /// <summary>
    /// True when no other process (cmd, PowerShell, IDE terminal) shares our console window,
    /// i.e. the window will vanish as soon as we exit.
    /// </summary>
    public static bool IsSoleOwner()
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected || Console.IsInputRedirected)
            return false;
        try
        {
            var list = new uint[2];
            var count = GetConsoleProcessList(list, (uint)list.Length);
            return count == 1;
        }
        catch
        {
            return false;
        }
    }
}
