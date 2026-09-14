using Avalonia.Data.Converters;
using Avalonia.Media;

namespace WorkFlowSync.App;

public static class Converters
{
    /// <summary>Status line colour: red for errors, theme foreground otherwise.</summary>
    public static readonly IValueConverter ErrorBrush =
        new FuncValueConverter<bool, IBrush>(isError => isError ? Brushes.Firebrick : Brushes.Gray);

    /// <summary>Pair state dot: green while the pair is checked automatically, grey while paused.</summary>
    public static readonly IValueConverter StateDot =
        new FuncValueConverter<bool, IBrush>(enabled => enabled
            ? new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x4F))
            : new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));
}
