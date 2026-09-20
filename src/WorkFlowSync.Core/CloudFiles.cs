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
}
