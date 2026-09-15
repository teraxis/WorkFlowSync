
using Avalonia.Media;
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

    /// <summary>Mirror with memory (default) or full two-way synchronisation.</summary>
    [ObservableProperty] private SyncMode _mode = SyncMode.Mirror;

    /// <summary>False = keep forever; true = keep only items first seen within <see cref="RetentionDays"/>.</summary>
    [ObservableProperty] private bool _hasRetention;
    [ObservableProperty] private int _retentionDays = 365;

    /// <summary>One exclude pattern per line.</summary>
    [ObservableProperty] private string _excludeText = "";

    public static IReadOnlyList<LinkMode> LinkModes { get; } = Enum.GetValues<LinkMode>();
    public static IReadOnlyList<SyncMode> Modes { get; } = Enum.GetValues<SyncMode>();

    public bool IsMirror => Mode == SyncMode.Mirror;

    /// <summary>Arrow between the two paths in the pair card: single for mirror, double for two-way.</summary>
    public Geometry? ModeIcon => Icon(IsMirror ? "IconArrowRight" : "IconArrowsBoth");

    public string ModeSummary => IsMirror
        ? "Дзеркало: нове з джерела копіюється сюди; ваші зміни в локальній папці остаточні"
        : "Повна синхронізація: усе, що стається в одній папці, стається і в іншій";

    public string SourceLabel => IsMirror ? "Мережева папка" : "Перша папка";
    public string TargetLabel => IsMirror ? "Локальна папка" : "Друга папка";

    public string ModeHint => IsMirror
        ? "Джерело лише читається. Видалене в джерелі лишається у вас; видалене чи змінене вами назад не повертається."
        : "Обидві папки рівноправні: створення, зміни й видалення переносяться в обидва боки. Видалене потрапляє в Кошик. Правила «видалене лишається» не діють.";

    public string RetentionHint => IsMirror
        ? "Старіші файли переносяться в Кошик і більше не повертаються. Файли, які ви редагували локально, це не зачіпає."
        : "У повній синхронізації строк зберігання не застосовується.";

    public string RetentionSummary => HasRetention ? $"{RetentionDays} дн." : "назавжди";

    /// <summary>True while this very pair is being synchronised (drives the row's buttons).</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Play/pause icon of the single state button (geometry from Styles/Icons.axaml).</summary>
    public Geometry? ToggleIcon => Icon(Enabled ? "IconPauseFill" : "IconPlayFill");
    public string ToggleGlyph => Enabled ? "⏸" : "▶";
    public string ToggleTip => Enabled ? "Призупинити автоматичну перевірку цієї пари" : "Запустити: перевіряти цю пару автоматично за інтервалом";
    public string StateSummary => IsBusy ? "перевіряється…" : Enabled ? "виконується" : "на паузі";

    /// <summary>One line of secondary facts under the paths.</summary>
    public string Facts
    {
        get
        {
            var parts = new List<string> { IsMirror ? "дзеркало" : "повна синхронізація", LinksSummary };
            if (IsMirror) parts.Add($"зберігати: {RetentionSummary}");
            if (ExcludeCount > 0) parts.Add($"виключень: {ExcludeCount}");
            return string.Join("   ·   ", parts);
        }
    }

    private static Geometry? Icon(string key)
    {
        var app = Avalonia.Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value as Geometry : null;
    }

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
        Mode = p.Mode,
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
        Mode = Mode,
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
        Mode = other.Mode;
        Links = other.Links;
        HasRetention = other.HasRetention;
        RetentionDays = other.RetentionDays;
        ExcludeText = other.ExcludeText;
        RaiseSummaries();
    }

    public void RaiseSummaries()
    {
        OnPropertyChanged(nameof(Facts));
        OnPropertyChanged(nameof(ToggleIcon));
        OnPropertyChanged(nameof(ToggleGlyph));
        OnPropertyChanged(nameof(ToggleTip));
        OnPropertyChanged(nameof(StateSummary));
        OnPropertyChanged(nameof(RetentionSummary));
        OnPropertyChanged(nameof(LinksSummary));
        OnPropertyChanged(nameof(ExcludeCount));
        RaiseMode();
    }

    private static List<string> ParseExcludes(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    partial void OnEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleIcon));
        OnPropertyChanged(nameof(ToggleGlyph));
        OnPropertyChanged(nameof(ToggleTip));
        OnPropertyChanged(nameof(StateSummary));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(StateSummary));

    partial void OnHasRetentionChanged(bool value) => RaiseFacts(nameof(RetentionSummary));
    partial void OnRetentionDaysChanged(int value) => RaiseFacts(nameof(RetentionSummary));
    partial void OnLinksChanged(LinkMode value) => RaiseFacts(nameof(LinksSummary));

    partial void OnModeChanged(SyncMode value)
    {
        RaiseMode();
        RaiseFacts(nameof(RetentionSummary));
    }

    private void RaiseMode()
    {
        OnPropertyChanged(nameof(IsMirror));
        OnPropertyChanged(nameof(ModeIcon));
        OnPropertyChanged(nameof(ModeSummary));
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(RetentionHint));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(TargetLabel));
    }
    partial void OnExcludeTextChanged(string value) => RaiseFacts(nameof(ExcludeCount));

    private void RaiseFacts(string name)
    {
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(Facts));
    }
}
