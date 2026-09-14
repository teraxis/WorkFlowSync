using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WorkFlowSync.Core.Execution;

/// <summary>Moves files/directories to the Windows Recycle Bin (works as a normal user; no confirmation UI).</summary>
public static class RecycleBin
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    // NB: Pack = 1 applies to 32-bit Windows only; on x64 the struct uses natural alignment (Pack = 1 crashes with AV).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>Sends one path to the Recycle Bin. Throws on failure (caller logs and continues).</summary>
    public static void Send(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Recycle Bin is Windows-only.");

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(fullPath) + "\0\0",     // double-null-terminated list
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var rc = SHFileOperationW(ref op);
        if (rc != 0 || op.fAnyOperationsAborted)
            throw new Win32Exception(rc, $"SHFileOperation failed (0x{rc:X}) for {fullPath}");
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new IOException($"Item still exists after recycle: {fullPath}");
    }
}
