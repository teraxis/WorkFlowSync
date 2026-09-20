using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace WorkFlowSync.Tests;

/// <summary>
/// A button class that repaints its background must also repaint its LABEL, not just its icon.
///
/// This exists because of a real bug: the primary button drew a white icon on blue while the text stayed
/// near-black. The button's Foreground is only inherited by its content, and an explicit &lt;TextBlock&gt;
/// inside the button is matched directly by the global `Selector="TextBlock"` rule — a setter on the element
/// itself wins over anything inherited. Colours in the palette were fine; the colour simply never reached
/// the text. Rendering cannot be asserted without a running Avalonia app, so the stylesheet is checked
/// instead: every button class with its own Foreground needs a matching `Button.<class> TextBlock` rule.
/// </summary>
public class ButtonLabelStyleTests
{
    private static string Controls([CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "WorkFlowSync.App", "Styles", "Controls.axaml"));

    /// <summary>Selector → the whole &lt;Style&gt; block, for every style in the sheet.</summary>
    private static List<(string Selector, string Body)> Styles()
    {
        var matches = Regex.Matches(Controls(), @"<Style\s+Selector=""(?<sel>[^""]+)""\s*>(?<body>.*?)</Style>", RegexOptions.Singleline);
        return matches.Select(m => (m.Groups["sel"].Value, m.Groups["body"].Value)).ToList();
    }

    [Fact]
    public void The_stylesheet_is_parsed_at_all()
    {
        var styles = Styles();

        Assert.NotEmpty(styles);
        Assert.Contains(styles, s => s.Selector == "TextBlock");
        Assert.Contains(styles, s => s.Selector == "Button.primary");
    }

    [Fact]
    public void Every_button_class_that_sets_its_own_text_colour_also_colours_its_label()
    {
        var styles = Styles();

        // Classes whose base style sets Foreground: those are the ones whose label must follow.
        var classes = styles
            .Where(s => Regex.IsMatch(s.Selector, @"^Button\.\w+$") && s.Body.Contains("Property=\"Foreground\"", StringComparison.Ordinal))
            .Select(s => s.Selector)
            .ToList();

        Assert.NotEmpty(classes);
        foreach (var cls in classes)
            Assert.True(styles.Any(s => s.Selector == $"{cls} TextBlock"),
                $"{cls} repaints itself but has no `{cls} TextBlock` rule — its label will stay the default near-black");
    }

    [Fact]
    public void The_primary_button_puts_its_label_on_the_accent_colour()
    {
        var label = Styles().Single(s => s.Selector == "Button.primary TextBlock");

        Assert.Contains("TextOnAccent", label.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_disabled_rule_comes_last_so_it_can_win()
    {
        var order = Styles().Select(s => s.Selector).ToList();
        var disabled = order.IndexOf("Button:disabled TextBlock");

        Assert.True(disabled >= 0, "a disabled button must grey its label");
        foreach (var cls in order.Where(s => Regex.IsMatch(s, @"^Button\.\w+ TextBlock$")))
            Assert.True(order.IndexOf(cls) < disabled,
                $"`{cls}` is declared after the disabled rule; in Avalonia the last match wins, so a disabled button would keep its colour");
    }

    [Fact]
    public void Label_rules_come_after_the_global_text_colour_they_have_to_override()
    {
        var order = Styles().Select(s => s.Selector).ToList();
        var global = order.IndexOf("TextBlock");

        foreach (var rule in order.Where(s => s.EndsWith(" TextBlock", StringComparison.Ordinal)))
            Assert.True(order.IndexOf(rule) > global,
                $"`{rule}` must be declared after the global TextBlock style, otherwise the global one wins");
    }
}
