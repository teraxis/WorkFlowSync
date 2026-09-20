using WorkFlowSync.Core;

namespace WorkFlowSync.Tests;

/// <summary>
/// Recognising a file by its name, for the type mark in the «Останні зміни» list. Extension-based on
/// purpose: the list is drawn from the state database, where the file may no longer exist, so nothing
/// here may touch the disk.
/// </summary>
public class FileKindsTests
{
    [Theory]
    [InlineData("протокол.docx", FileKind.Document)]
    [InlineData("звіт.DOC", FileKind.Document)]
    [InlineData("таблиця.xlsx", FileKind.Spreadsheet)]
    [InlineData("висновки.csv", FileKind.Spreadsheet)]
    [InlineData("презентація.pptx", FileKind.Presentation)]
    [InlineData("рішення.pdf", FileKind.Pdf)]
    [InlineData("WhatsApp Image 2026-08-11.jpeg", FileKind.Image)]
    [InlineData("Справи за п.3 ст.106.zip", FileKind.Archive)]
    [InlineData("keys.txt", FileKind.Text)]
    [InlineData("stop.bat", FileKind.Other)]
    [InlineData("без-розширення", FileKind.Other)]
    [InlineData("", FileKind.Other)]
    [InlineData(null, FileKind.Other)]
    public void A_file_is_recognised_by_its_extension(string? name, FileKind expected) =>
        Assert.Equal(expected, FileKinds.Of(name));

    [Theory]
    [InlineData("протокол.docx", "DOCX")]
    [InlineData("звіт.pdf", "PDF")]
    [InlineData("архів.tar.gz", "GZ")]                 // only the last extension matters
    [InlineData("дані.database", "DATA")]              // long ones are cut: a badge is a glance
    [InlineData("без-розширення", "•")]
    [InlineData(null, "•")]
    public void The_badge_is_short_and_upper_cased(string? name, string expected) =>
        Assert.Equal(expected, FileKinds.Badge(name));

    [Fact]
    public void A_name_with_dots_in_it_is_not_confused()
    {
        // «Справи за п.3 ст.106.zip» — dots inside the name are common in real documents.
        Assert.Equal(FileKind.Archive, FileKinds.Of("Справи за п.3 ст.106.zip"));
        Assert.Equal("ZIP", FileKinds.Badge("Справи за п.3 ст.106.zip"));
    }

    [Theory]
    [InlineData(FileKind.Document, "Accent")]
    [InlineData(FileKind.Spreadsheet, "Success")]
    [InlineData(FileKind.Pdf, "Danger")]
    [InlineData(FileKind.Other, "Neutral")]
    public void The_colour_comes_from_a_palette_token_not_a_hex_value(FileKind kind, string expected)
    {
        var key = FileKinds.PaletteKey(kind);

        Assert.Equal(expected, key);
        Assert.DoesNotContain("#", key, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_family_maps_to_a_token_that_has_a_soft_variant()
    {
        // The badge uses "<key>Soft" as its plate, so every key must have one in Palette.axaml.
        var soft = new[] { "Accent", "Success", "Warning", "Danger", "Neutral" };

        foreach (FileKind kind in Enum.GetValues<FileKind>())
            Assert.Contains(FileKinds.PaletteKey(kind), soft);
    }
}

/// <summary>
/// The shell-icon lookup. A Bitmap cannot be built without a running Avalonia app, so what is asserted
/// here is that the call is SAFE everywhere it is used — it must never throw, whatever it is handed.
/// Whether the icons actually appear is a visual check in the running program.
/// </summary>
public class SystemFileIconsTests
{
    [Theory]
    [InlineData("протокол.docx")]
    [InlineData("без-розширення")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(@"C:\шлях\з\роздільниками\файл.pdf")]
    [InlineData("дивні:символи?.zip")]
    public void Asking_for_an_icon_never_throws(string? name)
    {
        var ex = Record.Exception(() => WorkFlowSync.App.Views.SystemFileIcons.For(name));

        Assert.Null(ex);
    }

    [Fact]
    public void The_same_extension_is_asked_for_only_once()
    {
        // Cached per extension: the answer is the same for every .docx, and the shell call is not free.
        var first = WorkFlowSync.App.Views.SystemFileIcons.For("а.docx");
        var second = WorkFlowSync.App.Views.SystemFileIcons.For("б.docx");

        Assert.Same(first, second);
    }
}
