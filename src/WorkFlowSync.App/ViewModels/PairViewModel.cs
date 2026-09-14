using CommunityToolkit.Mvvm.ComponentModel;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.App.ViewModels;

/// <summary>Editable projection of <see cref="FolderPair"/> for the pair list and the pair dialog.</summary>
public sealed partial class PairViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _target = "";
    [ObservableProperty] private LinkMode _links = LinkMode.Follow;

    /// <summary>False = keep forever; true = keep only items first seen within <see cref="RetentionDays"/>.</summary>
    [ObservableProperty] private bool _hasRetention;
    [ObservableProperty] private int _retentionDays = 365;

    /// <summary>One exclude pattern per line.</summary>
    [ObservableProperty] private string _excludeText = "";

    public static IReadOnlyList<LinkMode> LinkModes { get; } = Enum.GetValues<LinkMode>();

    public string RetentionSummary => HasRetention ? $"{RetentionDays} дн." : "назавжди";

    /// <summary>True while this very pair is being synchronised (drives the row's buttons).</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Play/pause glyph of the single state button.</summary>
    public string ToggleGlyph => Enabled ? "⏸" : "▶";
    public string ToggleTip => Enabled ? "Призупинити автоматичну перевірку цієї пари" : "Запустити: перевіряти цю пару автоматично за інтервалом";
    public string StateSummary => IsBusy ? "перевіряється…" : Enabled ? "виконується" : "на паузі";

    public string LinksSummary => Links switch
    {
        LinkMode.Follow => "лінки: проходити",
        LinkMode.Skip => "лінки: пропускати",
        LinkMode.Recreate => "лінки: відтворювати",
        _ => Links.ToString(),
    };

    public int ExcludeCount => ParseExcludes(ExcludeText).Count;

    public static PairViewModel FromModel(FolderPair p) => new()
    {
        Name = p.Name,
        Enabled = p.Enabled,
        Source = p.Source,
        Target = p.Target,
        Links = p.Links,
        HasRetention = p.Retention is not null,
        RetentionDays = p.Retention is { } r ? Math.Max(1, (int)Math.Round(r.TotalDays)) : 365,
        ExcludeText = string.Join(Environment.NewLine, p.Exclude),
    };

    public FolderPair ToModel() => new()
    {
        Name = Name.Trim(),
        Enabled = Enabled,
        Source = Source.Trim(),
        Target = Target.Trim(),
        Links = Links,
        Retention = HasRetention ? TimeSpan.FromDays(RetentionDays) : null,
        Exclude = ParseExcludes(ExcludeText),
    };

    public PairViewModel Clone() => FromModel(ToModel());

    public void CopyFrom(PairViewModel other)
    {
        Name = other.Name;
        Enabled = other.Enabled;
        Source = other.Source;
        Target = other.Target;
        Links = other.Links;
        HasRetention = other.HasRetention;
        RetentionDays = other.RetentionDays;
        ExcludeText = other.ExcludeText;
        RaiseSummaries();
    }

    public void RaiseSummaries()
    {
        OnPropertyChanged(nameof(ToggleGlyph));
        OnPropertyChanged(nameof(ToggleTip));
        OnPropertyChanged(nameof(StateSummary));
        OnPropertyChanged(nameof(RetentionSummary));
        OnPropertyChanged(nameof(LinksSummary));
        OnPropertyChanged(nameof(ExcludeCount));
    }

    private static List<string> ParseExcludes(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    partial void OnEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleGlyph));
        OnPropertyChanged(nameof(ToggleTip));
        OnPropertyChanged(nameof(StateSummary));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(StateSummary));

    partial void OnHasRetentionChanged(bool value) => OnPropertyChanged(nameof(RetentionSummary));
    partial void OnRetentionDaysChanged(int value) => OnPropertyChanged(nameof(RetentionSummary));
    partial void OnLinksChanged(LinkMode value) => OnPropertyChanged(nameof(LinksSummary));
    partial void OnExcludeTextChanged(string value) => OnPropertyChanged(nameof(ExcludeCount));
}
