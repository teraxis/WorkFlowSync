namespace WorkFlowSync.Core;

/// <summary>
/// Google Drive integration and classification.
/// Supports Google Drive for desktop (mounted virtual disk G:\ or mirror folders),
/// filtering and skipping native Google web documents (.gdoc, .gsheet, etc.),
/// and discovery of active Google Drive roots.
/// </summary>
public static class GoogleDrive
{
    private static readonly HashSet<string> GoogleDocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gdoc",
        ".gsheet",
        ".gslides",
        ".gform",
        ".gdraw",
        ".gtable",
        ".jam",
        ".gmap",
        ".gsite",
        ".gshortcut",
        ".gvid",
        ".gscript",
    };

    /// <summary>
    /// Checks if a file is a native Google web document (Docs, Sheets, Slides, Forms, etc.).
    /// In Google Drive for desktop these are virtual pointer objects without regular binary file content.
    /// Attempting to read or copy their bytes throws IOException (Incorrect function).
    /// </summary>
    public static bool IsGoogleDoc(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return false;
        var ext = Path.GetExtension(pathOrName);
        return !string.IsNullOrEmpty(ext) && GoogleDocExtensions.Contains(ext);
    }

    /// <summary>
    /// Checks if the path represents a remote Google Drive cloud location (e.g. gdrive://account/folder).
    /// </summary>
    public static bool IsCloudPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.TrimStart().StartsWith("gdrive://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Formats a cloud folder into a persistent URI string.
    /// </summary>
    public static string FormatCloudPath(string accountName, string folderName, string accountId, string folderId)
    {
        var cleanName = string.IsNullOrWhiteSpace(folderName) ? "My Drive" : folderName;
        return $"gdrive://{Uri.EscapeDataString(accountName)}/{Uri.EscapeDataString(cleanName)}?id={Uri.EscapeDataString(folderId)}&acc={Uri.EscapeDataString(accountId)}";
    }

    /// <summary>
    /// Parses a cloud path URI into components.
    /// </summary>
    public static (string AccountName, string FolderName, string AccountId, string FolderId) ParseCloudPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsCloudPath(path))
            return ("", "", "", "");

        try
        {
            var raw = path.Trim();
            if (raw.StartsWith("gdrive://", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring("gdrive://".Length);

            var queryIdx = raw.IndexOf('?');
            var pathPart = queryIdx >= 0 ? raw.Substring(0, queryIdx) : raw;
            var queryPart = queryIdx >= 0 ? raw.Substring(queryIdx + 1) : "";

            var slashIdx = pathPart.IndexOf('/');
            var accountName = slashIdx >= 0
                ? Uri.UnescapeDataString(pathPart.Substring(0, slashIdx))
                : Uri.UnescapeDataString(pathPart);
            var folderName = slashIdx >= 0
                ? Uri.UnescapeDataString(pathPart.Substring(slashIdx + 1))
                : "My Drive";

            var query = System.Web.HttpUtility.ParseQueryString(queryPart);
            var folderId = query["id"] ?? "root";
            var accountId = query["acc"] ?? "";
            return (accountName, folderName, accountId, folderId);
        }
        catch
        {
            return ("", "", "", "");
        }
    }

    /// <summary>
    /// Checks whether the given path is located on Google Drive (cloud URI, mounted volume or local mirror folder).
    /// Safe to call from UI and planning threads; does not perform network I/O.
    /// </summary>
    public static bool IsGoogleDrivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (IsCloudPath(path)) return true;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && root.Length >= 2 && root[1] == ':')
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    if (string.Equals(drive.VolumeLabel, "Google Drive", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(drive.DriveFormat, "Google DriveFS", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            if (full.Contains(@"\Google Drive\", StringComparison.OrdinalIgnoreCase) ||
                full.EndsWith(@"\Google Drive", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
            // Invalid path or drive offline
        }
        return false;
    }

    /// <summary>
    /// True if Google Drive for desktop is currently mounted or available.
    /// </summary>
    public static bool IsInstalledOrMounted => GetMountedRoots().Count > 0;

    /// <summary>
    /// Finds all mounted Google Drive roots available on the system.
    /// </summary>
    public static IReadOnlyList<string> GetMountedRoots()
    {
        var list = new List<string>();
        if (!OperatingSystem.IsWindows()) return list;

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                if (string.Equals(drive.VolumeLabel, "Google Drive", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(drive.DriveFormat, "Google DriveFS", StringComparison.OrdinalIgnoreCase))
                {
                    var rootPath = drive.RootDirectory.FullName;
                    var myDriveUk = Path.Combine(rootPath, "Мій диск");
                    var myDriveEn = Path.Combine(rootPath, "My Drive");
                    if (Directory.Exists(myDriveUk)) list.Add(myDriveUk);
                    else if (Directory.Exists(myDriveEn)) list.Add(myDriveEn);
                    else list.Add(rootPath);
                }
            }
        }
        catch
        {
            // Permission or enumeration error
        }

        return list;
    }

    /// <summary>
    /// Checks whether the Google web document can be converted and exported to standard formats.
    /// Docs, Sheets, Slides, and Drawings have standard export formats; Forms, Sites, Maps, and Shortcuts do not.
    /// </summary>
    public static bool CanExport(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return false;
        var ext = Path.GetExtension(pathOrName);
        return ext.Equals(".gdoc", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gsheet", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gslides", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gdraw", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Translates source relative path (e.g. "Reports\doc.gdoc") to target destination path (e.g. "Reports\doc.docx").
    /// In Skip mode or for non-exportable types, returns the original path unchanged.
    /// </summary>
    public static string GetTargetRelativePath(string relPath, WorkFlowSync.Core.Config.GoogleDocExportMode mode)
    {
        if (string.IsNullOrWhiteSpace(relPath) || mode == WorkFlowSync.Core.Config.GoogleDocExportMode.Skip || !CanExport(relPath))
            return relPath;

        var ext = Path.GetExtension(relPath);
        if (mode == WorkFlowSync.Core.Config.GoogleDocExportMode.Pdf)
            return Path.ChangeExtension(relPath, ".pdf");

        // Office mode (default)
        if (ext.Equals(".gdoc", StringComparison.OrdinalIgnoreCase)) return Path.ChangeExtension(relPath, ".docx");
        if (ext.Equals(".gsheet", StringComparison.OrdinalIgnoreCase)) return Path.ChangeExtension(relPath, ".xlsx");
        if (ext.Equals(".gslides", StringComparison.OrdinalIgnoreCase)) return Path.ChangeExtension(relPath, ".pptx");
        if (ext.Equals(".gdraw", StringComparison.OrdinalIgnoreCase)) return Path.ChangeExtension(relPath, ".png");

        return Path.ChangeExtension(relPath, ".pdf");
    }

    /// <summary>
    /// Returns the Google Drive export MIME type for this document under the specified export mode.
    /// </summary>
    public static string GetExportMimeType(string relPath, WorkFlowSync.Core.Config.GoogleDocExportMode mode)
    {
        if (mode == WorkFlowSync.Core.Config.GoogleDocExportMode.Pdf)
            return "application/pdf";

        var ext = Path.GetExtension(relPath);
        if (ext.Equals(".gdoc", StringComparison.OrdinalIgnoreCase))
            return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        if (ext.Equals(".gsheet", StringComparison.OrdinalIgnoreCase))
            return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        if (ext.Equals(".gslides", StringComparison.OrdinalIgnoreCase))
            return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
        if (ext.Equals(".gdraw", StringComparison.OrdinalIgnoreCase))
            return "image/png";

        return "application/pdf";
    }

    /// <summary>
    /// Projects source directory entries so exportable Google web documents are represented by their
    /// target export relative paths (.docx, .xlsx, .pptx, .png, or .pdf) for one-way export to destination.
    /// Produces a target-to-source mapping dictionary for the planner and executor.
    /// </summary>
    public static IReadOnlyDictionary<string, WorkFlowSync.Core.Scanning.ScanEntry> MapGoogleDocsForExport(
        IReadOnlyDictionary<string, WorkFlowSync.Core.Scanning.ScanEntry> source,
        WorkFlowSync.Core.Config.GoogleDocExportMode mode,
        out Dictionary<string, string> targetToSourceMap)
    {
        targetToSourceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (mode == WorkFlowSync.Core.Config.GoogleDocExportMode.Skip || source.Count == 0)
            return source;

        var result = new Dictionary<string, WorkFlowSync.Core.Scanning.ScanEntry>(source.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, entry) in source)
        {
            if (entry.Kind == WorkFlowSync.Core.Model.EntryKind.File && IsGoogleDoc(path) && CanExport(path))
            {
                var targetRel = GetTargetRelativePath(path, mode);
                if (!source.ContainsKey(targetRel) && !result.ContainsKey(targetRel))
                {
                    targetToSourceMap[targetRel] = path;
                    result[targetRel] = entry with { RelativePath = targetRel };
                    continue;
                }
            }
            result[path] = entry;
        }
        return result;
    }

    /// <summary>
    /// Attempts to look up the Google Drive file ID from local Google DriveFS metadata sqlite databases.
    /// Safe to call while Google Drive is active; opens databases in read-only mode.
    /// </summary>
    public static string? TryLookupDriveFsFileId(string localFilePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(localFilePath))
            return null;

        try
        {
            var fileName = Path.GetFileName(localFilePath);
            if (string.IsNullOrEmpty(fileName)) return null;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var driveFsBase = Path.Combine(localAppData, "Google", "DriveFS");
            if (!Directory.Exists(driveFsBase)) return null;

            foreach (var dir in Directory.EnumerateDirectories(driveFsBase))
            {
                var dbPath = Path.Combine(dir, "metadata_sqlite_db");
                if (!File.Exists(dbPath)) continue;

                try
                {
                    var connStr = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    {
                        DataSource = dbPath,
                        Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                    }.ToString();

                    using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT id FROM items WHERE local_title = @title AND trashed = 0 LIMIT 1;";
                    cmd.Parameters.AddWithValue("@title", fileName);
                    var scalar = cmd.ExecuteScalar();
                    if (scalar is string id && !string.IsNullOrWhiteSpace(id))
                        return id;
                }
                catch
                {
                    // Continue to next account directory
                }
            }
        }
        catch
        {
            // Best effort
        }

        return null;
    }

    /// <summary>
    /// Resolves the Google Drive file ID for a local file, first through DriveFS metadata,
    /// then by checking local stub content if readable.
    /// </summary>
    public static string? TryGetFileId(string localFilePath)
    {
        var id = TryLookupDriveFsFileId(localFilePath);
        if (!string.IsNullOrEmpty(id)) return id;

        if (File.Exists(localFilePath))
        {
            try
            {
                // In some local sync mirrors or stubs, .gdoc is a text/JSON file containing doc_id or url
                var text = File.ReadAllText(localFilePath);
                var match = System.Text.RegularExpressions.Regex.Match(text, @"(?:/d/|""doc_id""\s*:\s*""|""id""\s*:\s*"")([a-zA-Z0-9_-]{20,})");
                if (match.Success)
                    return match.Groups[1].Value;
            }
            catch
            {
                // Not readable or virtual stub
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the primary mounted Google Drive root, or null if none is found.
    /// </summary>
    public static string? GetPrimaryRoot() => GetMountedRoots().FirstOrDefault();
}
