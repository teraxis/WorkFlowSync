using Avalonia.Data.Converters;
using Avalonia.Media;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.I18n;

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
            SyncMode.TwoWay => I18n.T("enum.mode.twoway"),
            _ => I18n.T("enum.mode.mirror"),
        });

    public static readonly IValueConverter WatchName =
        new FuncValueConverter<WatchMode, string>(w => w switch
        {
            WatchMode.On => I18n.T("enum.watch.on"),
            WatchMode.Off => I18n.T("enum.watch.off"),
            _ => I18n.T("enum.watch.auto"),
        });

    public static readonly IValueConverter CloudFilesName =
        new FuncValueConverter<CloudFileMode, string>(m => m switch
        {
            CloudFileMode.Hydrate => I18n.T("enum.cloud.hydrate"),
            _ => I18n.T("enum.cloud.skip"),
        });

    public static readonly IValueConverter LinkName =
        new FuncValueConverter<LinkMode, string>(l => l switch
        {
            LinkMode.Follow => I18n.T("enum.link.follow"),
            LinkMode.Skip => I18n.T("enum.link.skip"),
            LinkMode.Recreate => I18n.T("enum.link.recreate"),
            _ => l.ToString(),
        });

    /// <summary>Soft plate behind a file-type badge; the family decides, the palette supplies the colour.</summary>
    public static readonly IValueConverter FileKindPlate =
        new FuncValueConverter<FileKind, IBrush>(k => Brush(FileKinds.PaletteKey(k) + "Soft", true)!);

    /// <summary>The badge's own text colour, from the same family.</summary>
    public static readonly IValueConverter FileKindInk =
        new FuncValueConverter<FileKind, IBrush>(k =>
        {
            // Accent has a dedicated readable-on-soft token; the semantic colours already are one.
            var key = FileKinds.PaletteKey(k);
            return Brush(key == "Accent" ? "AccentText" : key, true)!;
        });

    public static readonly IValueConverter NotifyName =
        new FuncValueConverter<NotifyMode, string>(m => m switch
        {
            NotifyMode.ErrorsOnly => I18n.T("enum.notify.errors"),
            NotifyMode.Off => I18n.T("enum.notify.off"),
            _ => I18n.T("enum.notify.all"),
        });

    public static readonly IValueConverter CpuLoadName =
        new FuncValueConverter<CpuLoad, string>(l => l switch
        {
            CpuLoad.Full => I18n.T("enum.cpu.full"),
            CpuLoad.Low => I18n.T("enum.cpu.low"),
            _ => I18n.T("enum.cpu.balanced"),
        });

    public static readonly IValueConverter ThemeName =
        new FuncValueConverter<AppTheme, string>(t => t switch
        {
            AppTheme.Light => I18n.T("enum.theme.light"),
            AppTheme.Dark => I18n.T("enum.theme.dark"),
            _ => I18n.T("enum.theme.system"),
        });

    public static readonly IValueConverter LanguageName =
        new FuncValueConverter<string, string>(l => (l?.ToLowerInvariant()) switch
        {
            "en" => I18n.T("enum.lang.en"),
            "uk" => I18n.T("enum.lang.uk"),
            _ => I18n.T("enum.lang.system"),
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
