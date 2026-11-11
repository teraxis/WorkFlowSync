using Microsoft.Win32;

namespace WorkFlowSync.Core.Planning;

/// <summary>Effective local path ceiling from user configuration, Windows and OneDrive.</summary>
public static class PathLimits
{
    public const int WindowsLegacyMax = 259;
    public const int OneDriveRelativeMax = 400;

    public static int Effective(string targetRoot, int configured) =>
        Compute(targetRoot, configured, WindowsLongPathsEnabled(), OneDriveRoot(targetRoot));

    public static int Compute(string targetRoot, int configured, bool longPathsEnabled, string? oneDriveRoot)
    {
        var result = longPathsEnabled ? configured : Math.Min(configured, WindowsLegacyMax);
        if (oneDriveRoot is not null)
            result = Math.Min(result, oneDriveRoot.TrimEnd('\\', '/').Length + 1 + OneDriveRelativeMax);
        return result;
    }

    public static bool WindowsLongPathsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return true;
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
        return key?.GetValue("LongPathsEnabled") is int enabled && enabled != 0;
    }

    public static string? OneDriveRoot(string targetRoot)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
        if (accounts is null) return null;
        var target = Path.GetFullPath(targetRoot).TrimEnd('\\', '/');
        string? best = null;
        foreach (var account in accounts.GetSubKeyNames())
        {
            using var key = accounts.OpenSubKey(account);
            if (key?.GetValue("UserFolder") is not string folder || string.IsNullOrWhiteSpace(folder)) continue;
            var root = Path.GetFullPath(folder).TrimEnd('\\', '/');
            if (!target.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                !target.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null || root.Length > best.Length) best = root;
        }
        return best;
    }
}
