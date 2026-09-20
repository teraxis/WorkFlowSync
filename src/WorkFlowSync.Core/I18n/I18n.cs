using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace WorkFlowSync.Core.I18n;

/// <summary>
/// Core localization engine for WorkFlowSync.
/// Loads standard JSON locale definitions from embedded resources (or external override files),
/// supports instant runtime switching with INotifyPropertyChanged indexing for UI bindings,
/// and falls back gracefully to default translations.
/// </summary>
public sealed class I18n : INotifyPropertyChanged
{
    private static readonly Lazy<I18n> LazyInstance = new(() => new I18n());
    public static I18n Instance => LazyInstance.Value;

    public const string DefaultLanguage = "uk";
    public const string EnglishLanguage = "en";

    private readonly Dictionary<string, string> _currentTranslations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fallbackTranslations = new(StringComparer.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    public string CurrentLanguage { get; private set; } = DefaultLanguage;

    public I18n()
    {
        LoadFallback();
        SetLanguage(DefaultLanguage);
    }

    /// <summary>Indexer used for direct reactive bindings in XAML, e.g. {Binding [nav.tasks], Source={x:Static i18n:I18n.Instance}}</summary>
    public string this[string key] => Translate(key);

    /// <summary>Translates a key into the currently active language, with fallback.</summary>
    public static string T(string key) => Instance.Translate(key);

    /// <summary>Translates a key and formats it with positional parameters.</summary>
    public static string T(string key, params object[] args)
    {
        var template = T(key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public string Translate(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        if (_currentTranslations.TryGetValue(key, out var val)) return val;
        if (_fallbackTranslations.TryGetValue(key, out var fallback)) return fallback;
        return key;
    }

    /// <summary>
    /// Switches the active language ("uk", "en", or "system") and notifies all bound UI elements.
    /// </summary>
    public CultureInfo Culture => new(CurrentLanguage);

    public void SetLanguage(string languageCode)
    {
        var resolved = ResolveLanguageCode(languageCode);
        if (resolved == CurrentLanguage && _currentTranslations.Count > 0) return;

        CurrentLanguage = resolved;
        try
        {
            var culture = new CultureInfo(resolved);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
        }
        catch
        {
            // Ignore culture creation issues on restricted environments
        }

        _currentTranslations.Clear();

        var loaded = LoadTranslations(resolved);
        foreach (var (k, v) in loaded)
        {
            _currentTranslations[k] = v;
        }

        // Raise property change for indexer bindings in Avalonia
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
        foreach (var k in _currentTranslations.Keys)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs($"[{k}]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs($"Item[{k}]"));
        }
        LanguageChanged?.Invoke();
    }

    public static string ResolveLanguageCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            var uiLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            return uiLang == "uk" ? "uk" : "en";
        }

        return code.ToLowerInvariant() switch
        {
            "uk" or "ua" or "ukr" => "uk",
            "en" or "eng" => "en",
            _ => "uk"
        };
    }

    private void LoadFallback()
    {
        var fallbackDict = LoadTranslations(DefaultLanguage);
        foreach (var (k, v) in fallbackDict)
        {
            _fallbackTranslations[k] = v;
        }
    }

    private static Dictionary<string, string> LoadTranslations(string lang)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. First check if an external override file exists in a 'locales' directory next to the exe
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var externalPath = Path.Combine(baseDir, "locales", $"{lang}.json");
            if (File.Exists(externalPath))
            {
                var content = File.ReadAllText(externalPath);
                ParseInto(content, dict);
                if (dict.Count > 0) return dict;
            }
        }
        catch
        {
            // Fall through to embedded resource
        }

        // 2. Load embedded resource from assembly
        try
        {
            var assembly = typeof(I18n).Assembly;
            var resourceName = $"WorkFlowSync.Core.Locales.{lang}.json";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();
                ParseInto(content, dict);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[I18n] Failed to load embedded locale {lang}: {ex.Message}");
        }

        return dict;
    }

    private static void ParseInto(string json, Dictionary<string, string> dict)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                dict[prop.Name] = prop.Value.GetString() ?? "";
            }
        }
    }
}
