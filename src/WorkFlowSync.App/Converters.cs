using Avalonia.Data.Converters;
using Avalonia.Media;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.App;

/// <summary>View-only formatting helpers. Colours come from the theme palette, never hard-coded twice.</summary>
public static class Converters
{
    /// <summary>Status line colour: red for errors, muted otherwise.</summary>
    public static readonly IValueConverter StatusText =
        new FuncValueConverter<bool, IBrush>(isError => Brush("Danger", isError) ?? Brush("TextSecondary", true)!);

    /// <summary>Kept for compatibility with earlier bindings.</summary>
    public static readonly IValueConverter ErrorBrush = StatusText;

    /// <summary>Pair state dot: green while the pair is checked automatically, grey while paused.</summary>
    public static readonly IValueConverter StateDot =
        new FuncValueConverter<bool, IBrush>(on => Brush(on ? "Success" : "Neutral", true)!);

    /// <summary>Soft halo behind the dot, same hue as the dot.</summary>
    public static readonly IValueConverter StateHalo =
        new FuncValueConverter<bool, IBrush>(on => Brush(on ? "SuccessSoft" : "NeutralSoft", true)!);

    public static readonly IValueConverter ModeName =
        new FuncValueConverter<SyncMode, string>(m => m switch
        {
            SyncMode.TwoWay => "Повна синхронізація (в обидва боки)",
            _ => "Дзеркало (в один бік)",
        });

    public static readonly IValueConverter CpuLoadName =
        new FuncValueConverter<CpuLoad, string>(l => l switch
        {
            CpuLoad.Full => "Повне — найшвидше",
            CpuLoad.Low => "Низьке — не заважати роботі",
            _ => "Помірне — рекомендовано",
        });

    public static readonly IValueConverter ThemeName =
        new FuncValueConverter<AppTheme, string>(t => t switch
        {
            AppTheme.Light => "Світла",
            AppTheme.Dark => "Темна",
            _ => "Як у Windows",
        });

    /// <summary>Looks a brush up in the application resources for the current theme variant.</summary>
    private static IBrush? Brush(string key, bool wanted)
    {
        if (!wanted) return null;
        var app = Avalonia.Application.Current;
        if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush)
            return brush;
        return Brushes.Gray;
    }
}
