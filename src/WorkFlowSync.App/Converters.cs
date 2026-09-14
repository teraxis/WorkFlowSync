using Avalonia.Data.Converters;
using Avalonia.Media;

namespace WorkFlowSync.App;

public static class Converters
{
    /// <summary>Status line colour: red for errors, theme foreground otherwise.</summary>
    public static readonly IValueConverter ErrorBrush =
        new FuncValueConverter<bool, IBrush>(isError => isError ? Brushes.Firebrick : Brushes.Gray);
}
