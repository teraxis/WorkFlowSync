using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WorkFlowSync.Core;

/// <summary>
/// Files On-Demand placeholders (OneDrive and other cloud providers). A placeholder looks like an ordinary
/// file in a listing — full logical size, real mtime — but its bytes are not on disk: reading it downloads
/// the whole thing. Scanning must therefore ignore these flags, and copying must respect them.
/// </summary>
public static class CloudFiles
{
    // Undocumented in System.IO but stable: FILE_ATTRIBUTE_RECALL_ON_OPEN / _RECALL_ON_DATA_ACCESS.
    public const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    public const FileAttributes Pinned = (FileAttributes)0x80000;
    public const FileAttributes Unpinned = (FileAttributes)0x100000;
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    private const FileAttributes RecallFlags = RecallOnOpen | RecallOnDataAccess;

    /// <summary>True when the bytes live in the cloud and opening the file would fetch them.</summary>
    public static bool IsPlaceholder(FileAttributes attributes) =>
        (attributes & RecallFlags) != 0 || (attributes & FileAttributes.Offline) != 0;

    /// <summary>Same question about a path. Unreadable attributes answer "no" — the copy will fail on its own merits.</summary>
    public static bool IsPlaceholder(string path)
    {
        try
        {
            return IsPlaceholder(File.GetAttributes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether placeholders are even possible on this root. Files On-Demand is a local filesystem feature,
    /// so checking a network path would only buy a round trip per file for an answer that is always "no".
    /// </summary>
    public static bool Possible(string root) => RootProbe.QuickKind(root) is RootKind.Local;

    /// <summary>True only for a local path managed by a registered Windows Files On-Demand provider.</summary>
    public static bool IsSyncRoot(string path) =>
        Possible(path) && new WindowsCloudFilePlatform().IsSyncRoot(path);

    /// <summary>The attribute change documented by Microsoft as the scriptable equivalent of «Free up space».</summary>
    public static FileAttributes OnlineOnlyAttributes(FileAttributes attributes) =>
        (attributes | Unpinned) & ~Pinned;
}

/// <summary>Disk capacity reported for the volume that contains a cloud sync root.</summary>
public readonly record struct CloudDiskSpace(long AvailableBytes, long TotalBytes);

/// <summary>
/// Result of asking Windows whether a path belongs to a registered Files On-Demand provider.
/// Rotation uses this only to distinguish cloud placeholders from unrelated reparse points.
/// </summary>
public enum CloudRootStatus
{
    NotCloud,
    Cloud,
    Unknown,
}

/// <summary>
/// Narrow seam around Windows Files On-Demand. Production uses <see cref="WindowsCloudFilePlatform"/>;
/// tests replace it so no real OneDrive folder or account is required.
/// </summary>
public interface ICloudFilePlatform
{
    bool IsSyncRoot(string path);
    CloudRootStatus GetSyncRootStatus(string path) =>
        IsSyncRoot(path) ? CloudRootStatus.Cloud : CloudRootStatus.NotCloud;
    CloudDiskSpace GetDiskSpace(string path);
    void RequestOnlineOnly(string path);
}

/// <summary>Windows Cloud Files API + the documented Pinned/Unpinned file attributes.</summary>
public sealed class WindowsCloudFilePlatform : ICloudFilePlatform
{
    private const int CfSyncRootInfoBasic = 0;
    private const int ErrorNotUnderCloudSyncRoot = 390;

    [DllImport("CldApi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CfGetSyncRootInfoByPath(
        string filePath,
        int infoClass,
        out long infoBuffer,
        uint infoBufferLength,
        out uint returnedLength);

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    public bool IsSyncRoot(string path) => GetSyncRootStatus(path) == CloudRootStatus.Cloud;

    public CloudRootStatus GetSyncRootStatus(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return CloudRootStatus.Unknown;
        try
        {
            // A task may point at a child folder that has not been created yet. Its nearest existing
            // ancestor is enough: if that ancestor belongs to a sync root, the new child will too.
            var candidate = Path.GetFullPath(path);
            while (!Directory.Exists(candidate))
            {
                var parent = Directory.GetParent(candidate);
                if (parent is null) return CloudRootStatus.Unknown;
                candidate = parent.FullName;
            }

            // Windows Cloud Files placeholders only live on supported local volumes. UNC shares, mapped
            // network drives and removable media are therefore confirmed non-cloud without calling CfAPI.
            var shape = RootProbe.Shape(candidate);
            if (shape.IsUnc) return CloudRootStatus.NotCloud;
            var root = Path.GetPathRoot(candidate);
            if (!string.IsNullOrEmpty(root))
            {
                var driveType = new DriveInfo(root).DriveType;
                if (driveType is DriveType.Network or DriveType.Removable or DriveType.CDRom)
                    return CloudRootStatus.NotCloud;
            }

            var hr = CfGetSyncRootInfoByPath(candidate, CfSyncRootInfoBasic,
                out _, sizeof(long), out _);
            if (hr >= 0) return CloudRootStatus.Cloud;

            // ERROR_NOT_A_CLOUD_SYNC_ROOT is the one negative answer that proves permanent rotation may
            // be considered. Any other API/access/path failure is indeterminate and therefore fail-closed.
            var win32 = hr & 0xffff;
            return win32 == ErrorNotUnderCloudSyncRoot
                ? CloudRootStatus.NotCloud
                : CloudRootStatus.Unknown;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            return CloudRootStatus.Unknown;
        }
    }

    public CloudDiskSpace GetDiskSpace(string path)
    {
        var full = Path.GetFullPath(path);
        if (!GetDiskFreeSpaceEx(full, out var available, out var total, out _))
            throw new IOException($"Cannot determine disk space for '{path}'.", new Win32Exception(Marshal.GetLastWin32Error()));
        return new CloudDiskSpace(checked((long)available), checked((long)total));
    }

    public void RequestOnlineOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, CloudFiles.OnlineOnlyAttributes(attributes));
    }
}
