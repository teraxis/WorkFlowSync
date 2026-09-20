using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Data;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.I18n;
using Xunit;

namespace WorkFlowSync.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void EmbeddedLocales_ArePresentAndParseable()
    {
        var assembly = typeof(I18n).Assembly;
        var ukStream = assembly.GetManifestResourceStream("WorkFlowSync.Core.Locales.uk.json");
        var enStream = assembly.GetManifestResourceStream("WorkFlowSync.Core.Locales.en.json");

        Assert.NotNull(ukStream);
        Assert.NotNull(enStream);

        using var ukDoc = JsonDocument.Parse(ukStream);
        using var enDoc = JsonDocument.Parse(enStream);

        Assert.NotEmpty(ukDoc.RootElement.EnumerateObject().ToList());
        Assert.NotEmpty(enDoc.RootElement.EnumerateObject().ToList());
    }

    [Fact]
    public void KeyParity_UkAndEnHaveExactSameKeys()
    {
        var assembly = typeof(I18n).Assembly;
        using var ukStream = assembly.GetManifestResourceStream("WorkFlowSync.Core.Locales.uk.json")!;
        using var enStream = assembly.GetManifestResourceStream("WorkFlowSync.Core.Locales.en.json")!;

        using var ukDoc = JsonDocument.Parse(ukStream);
        using var enDoc = JsonDocument.Parse(enStream);

        var ukKeys = ukDoc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enKeys = enDoc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingInEn = ukKeys.Except(enKeys).ToList();
        var missingInUk = enKeys.Except(ukKeys).ToList();

        Assert.True(missingInEn.Count == 0, $"Keys present in uk.json but missing in en.json: {string.Join(", ", missingInEn)}");
        Assert.True(missingInUk.Count == 0, $"Keys present in en.json but missing in uk.json: {string.Join(", ", missingInUk)}");
    }

    [Fact]
    public void NoEmptyTranslations()
    {
        var assembly = typeof(I18n).Assembly;
        foreach (var lang in new[] { "uk", "en" })
        {
            using var stream = assembly.GetManifestResourceStream($"WorkFlowSync.Core.Locales.{lang}.json")!;
            using var doc = JsonDocument.Parse(stream);

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var val = prop.Value.GetString();
                Assert.False(string.IsNullOrWhiteSpace(val), $"Key '{prop.Name}' in '{lang}.json' has an empty value");
            }
        }
    }

    [Fact]
    public void PlaceholderParity_PlaceholdersMatchBetweenLocales()
    {
        var assembly = typeof(I18n).Assembly;
        using var ukStream = assembly.GetManifestResourceStream("WorkFlowSync.Core.Locales.uk.json")!;
        using var enStream = assembly.GetManifestResourceStream("WorkFlowSync.Core.Locales.en.json")!;

        using var ukDoc = JsonDocument.Parse(ukStream);
        using var enDoc = JsonDocument.Parse(enStream);

        var ukDict = ukDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        var enDict = enDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

        var placeholderRegex = new Regex(@"\{(\d+)\}", RegexOptions.Compiled);

        foreach (var (key, ukVal) in ukDict)
        {
            if (!enDict.TryGetValue(key, out var enVal)) continue;

            var ukPlaceholders = placeholderRegex.Matches(ukVal).Select(m => m.Value).OrderBy(x => x).ToList();
            var enPlaceholders = placeholderRegex.Matches(enVal).Select(m => m.Value).OrderBy(x => x).ToList();

            Assert.True(ukPlaceholders.SequenceEqual(enPlaceholders),
                $"Placeholders mismatch for key '{key}': uk has [{string.Join(", ", ukPlaceholders)}] while en has [{string.Join(", ", enPlaceholders)}]");
        }
    }

    [Fact]
    public void SetLanguage_SwitchesTranslationsDynamically()
    {
        var i18n = new I18n();

        i18n.SetLanguage("uk");
        Assert.Equal("uk", i18n.CurrentLanguage);
        Assert.Equal("WorkFlowSync", i18n["app.title"]);
        Assert.Equal("Завдання", i18n["nav.tasks"]);

        i18n.SetLanguage("en");
        Assert.Equal("en", i18n.CurrentLanguage);
        Assert.Equal("WorkFlowSync", i18n["app.title"]);
        Assert.Equal("Tasks", i18n["nav.tasks"]);
    }

    [Fact]
    public void LocExtension_BindingUpdatesOnLanguageChange()
    {
        try
        {
            I18n.Instance.SetLanguage("uk");
            var ext = new WorkFlowSync.App.Localization.LocExtension("nav.tasks");
            var binding = (Binding)ext.ProvideValue(null!);

            var textBlock = new Avalonia.Controls.TextBlock();
            textBlock.Bind(Avalonia.Controls.TextBlock.TextProperty, binding);

            Assert.Equal("Завдання", textBlock.Text);

            I18n.Instance.SetLanguage("en");
            Assert.Equal("Tasks", textBlock.Text);
        }
        finally
        {
            I18n.Instance.SetLanguage("uk");
        }
    }

    [Fact]
    public void FallbackBehavior_ReturnsKeyIfMissing()
    {
        var i18n = new I18n();
        var result = i18n.Translate("non.existent.key.xyz");
        Assert.Equal("non.existent.key.xyz", result);
    }

    [Fact]
    public void ResolveLanguageCode_HandlesAliases()
    {
        Assert.Equal("uk", I18n.ResolveLanguageCode("uk"));
        Assert.Equal("uk", I18n.ResolveLanguageCode("ua"));
        Assert.Equal("uk", I18n.ResolveLanguageCode("ukr"));
        Assert.Equal("en", I18n.ResolveLanguageCode("en"));
        Assert.Equal("en", I18n.ResolveLanguageCode("eng"));
        Assert.Contains(I18n.ResolveLanguageCode("system"), new[] { "uk", "en" });
        Assert.Equal("uk", I18n.ResolveLanguageCode("invalid_code"));
    }

    [Fact]
    public void SyncConfig_ValidatesLanguage()
    {
        var cfg = new SyncConfig
        {
            Pairs = new()
            {
                new FolderPair { Name = "test", Source = @"C:\src", Target = @"C:\dst" }
            },
            Language = "uk"
        };
        Assert.Empty(cfg.Validate());

        cfg.Language = "en";
        Assert.Empty(cfg.Validate());

        cfg.Language = "system";
        Assert.Empty(cfg.Validate());

        cfg.Language = "de";
        var errs = cfg.Validate().ToList();
        Assert.Contains(errs, e => e.Contains("Language"));
    }
}
