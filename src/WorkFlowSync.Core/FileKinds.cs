namespace WorkFlowSync.Core;

/// <summary>Family of a file, for showing it in a list the way a person recognises it.</summary>
public enum FileKind
{
    Other,
    Document,
    Spreadsheet,
    Presentation,
    Pdf,
    Image,
    Archive,
    Text,
}

/// <summary>
/// Recognises what kind of file a name refers to. Pure and extension-based on purpose: the list is drawn
/// from the state database, where the file may no longer exist, so nothing here may touch the disk.
/// </summary>
public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = FileKind.Document, [".docx"] = FileKind.Document, [".odt"] = FileKind.Document,
        [".rtf"] = FileKind.Document, [".pages"] = FileKind.Document,

        [".xls"] = FileKind.Spreadsheet, [".xlsx"] = FileKind.Spreadsheet, [".xlsm"] = FileKind.Spreadsheet,
        [".ods"] = FileKind.Spreadsheet, [".csv"] = FileKind.Spreadsheet,

        [".ppt"] = FileKind.Presentation, [".pptx"] = FileKind.Presentation, [".odp"] = FileKind.Presentation,

        [".pdf"] = FileKind.Pdf,

        [".jpg"] = FileKind.Image, [".jpeg"] = FileKind.Image, [".png"] = FileKind.Image,
        [".gif"] = FileKind.Image, [".bmp"] = FileKind.Image, [".webp"] = FileKind.Image,
        [".heic"] = FileKind.Image, [".tif"] = FileKind.Image, [".tiff"] = FileKind.Image,
        [".svg"] = FileKind.Image,

        [".zip"] = FileKind.Archive, [".rar"] = FileKind.Archive, [".7z"] = FileKind.Archive,
        [".tar"] = FileKind.Archive, [".gz"] = FileKind.Archive, [".cab"] = FileKind.Archive,

        [".txt"] = FileKind.Text, [".log"] = FileKind.Text, [".md"] = FileKind.Text,
        [".json"] = FileKind.Text, [".xml"] = FileKind.Text, [".ini"] = FileKind.Text,
    };

    public static FileKind Of(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return FileKind.Other;
        var ext = Path.GetExtension(fileName);
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var kind) ? kind : FileKind.Other;
    }

    /// <summary>
    /// Short label for the badge: the extension without its dot, upper-cased. Long ones are cut — a badge
    /// is a glance, not a reading exercise — and a file without an extension gets a neutral mark.
    /// </summary>
    public static string Badge(string? fileName)
    {
        var ext = string.IsNullOrWhiteSpace(fileName) ? "" : Path.GetExtension(fileName).TrimStart('.');
        if (ext.Length == 0) return "•";
        return ext.Length <= 4 ? ext.ToUpperInvariant() : ext[..4].ToUpperInvariant();
    }

    /// <summary>
    /// Palette key for the badge's colour family. Returns the name of a semantic token, not a colour:
    /// colours live in Palette.axaml and nowhere else (docs/product/features/design-system.md).
    /// </summary>
    public static string PaletteKey(FileKind kind) => kind switch
    {
        FileKind.Document => "Accent",
        FileKind.Spreadsheet => "Success",
        FileKind.Presentation => "Warning",
        FileKind.Pdf => "Danger",
        FileKind.Image => "Warning",
        FileKind.Archive => "Neutral",
        FileKind.Text => "Neutral",
        _ => "Neutral",
    };
}
