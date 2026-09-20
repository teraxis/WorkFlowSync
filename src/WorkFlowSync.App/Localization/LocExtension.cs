using Avalonia.Data;
using Avalonia.Markup.Xaml;
using WorkFlowSync.Core.I18n;

namespace WorkFlowSync.App.Localization;

/// <summary>
/// XAML markup extension for localization:
/// Usage in XAML: Text="{loc:Loc nav.tasks}" or Content="{loc:Loc common.save}"
/// Reacts dynamically to I18n.Instance.SetLanguage(...) without restarting the window.
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public LocExtension() { }

    public LocExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"[{Key}]")
        {
            Source = I18n.Instance,
            Mode = BindingMode.OneWay
        };
    }
}
