using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace WorkFlowSync.Tests;

/// <summary>
/// Guards the readability of the design system's colour pairs (docs/product/features/design-system.md).
/// The palette is read from disk rather than from a running app: brushes only resolve inside Avalonia,
/// and the point here is to catch a bad colour at build time, not at run time.
///
/// Thresholds are WCAG 2.1: 4.5:1 for normal text, 7:1 for the AAA level the primary button now meets.
/// </summary>
public class PaletteContrastTests
{
    private static string PalettePath([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "WorkFlowSync.App", "Styles", "Palette.axaml");

    private static Dictionary<string, string> Theme(string name)
    {
        var xaml = File.ReadAllText(PalettePath());
        var darkAt = xaml.IndexOf("x:Key=\"Dark\"", StringComparison.Ordinal);
        Assert.True(darkAt > 0, "Palette.axaml must keep a Light and a Dark ResourceDictionary");
        var section = name == "Dark" ? xaml[darkAt..] : xaml[..darkAt];

        var colours = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(section, @"x:Key=""(?<k>\w+)""\s+Color=""(?<c>#[0-9A-Fa-f]{6})"""))
            colours[m.Groups["k"].Value] = m.Groups["c"].Value;
        return colours;
    }

    /// <summary>WCAG relative luminance of an #RRGGBB colour.</summary>
    private static double Luminance(string hex)
    {
        double Channel(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        var r = int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    private static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void The_maths_matches_the_known_reference_values()
    {
        Assert.Equal(21.0, Contrast("#000000", "#FFFFFF"), 1);
        Assert.Equal(1.0, Contrast("#123456", "#123456"), 1);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Button_labels_stay_readable_on_every_accent_shade(string theme)
    {
        var c = Theme(theme);

        foreach (var shade in new[] { "Accent", "AccentHover", "AccentPressed" })
        {
            var ratio = Contrast(c["TextOnAccent"], c[shade]);
            Assert.True(ratio >= 4.5,
                $"{theme}: TextOnAccent {c["TextOnAccent"]} on {shade} {c[shade]} is {ratio:N2}:1, below the 4.5:1 minimum");
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void The_primary_button_meets_the_stricter_level_it_was_tuned_for(string theme)
    {
        var c = Theme(theme);
        var ratio = Contrast(c["TextOnAccent"], c["Accent"]);
        var required = theme == "Light" ? 7.0 : 5.0;   // dark keeps a bright accent, so it clears AA, not AAA

        Assert.True(ratio >= required,
            $"{theme}: primary button is {ratio:N2}:1, below the {required:N1}:1 this palette is tuned for");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void The_soft_accent_button_is_readable_too(string theme)
    {
        var c = Theme(theme);

        // The pause button on a pair row: AccentText on AccentSoft.
        var ratio = Contrast(c["AccentText"], c["AccentSoft"]);

        Assert.True(ratio >= 4.5, $"{theme}: AccentText on AccentSoft is {ratio:N2}:1, below the 4.5:1 minimum");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Every_text_shade_is_readable_on_the_surfaces_it_is_used_on(string theme)
    {
        var c = Theme(theme);

        foreach (var surface in new[] { "SurfaceCanvas", "SurfaceCard", "SurfaceRail", "SurfaceSunken" })
        {
            Assert.True(Contrast(c["TextPrimary"], c[surface]) >= 7.0,
                $"{theme}: TextPrimary on {surface} is {Contrast(c["TextPrimary"], c[surface]):N2}:1");

            // TextMuted is not decoration: the pair card writes both folder paths and the facts line in it.
            // Leaving it out of this check is exactly how it drifted to 3.48:1 and became unreadable.
            foreach (var shade in new[] { "TextSecondary", "TextMuted" })
                Assert.True(Contrast(c[shade], c[surface]) >= 4.5,
                    $"{theme}: {shade} {c[shade]} on {surface} {c[surface]} is {Contrast(c[shade], c[surface]):N2}:1, below the 4.5:1 minimum");
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void The_three_text_shades_stay_distinguishable_from_each_other(string theme)
    {
        var c = Theme(theme);

        // Readability must not be bought by collapsing the hierarchy into one grey.
        Assert.True(Contrast(c["TextPrimary"], c["TextMuted"]) >= 1.6,
            $"{theme}: TextPrimary and TextMuted are too close ({Contrast(c["TextPrimary"], c["TextMuted"]):N2}:1)");
        Assert.NotEqual(c["TextSecondary"], c["TextMuted"]);
    }
}
