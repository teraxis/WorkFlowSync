using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.I18n;

namespace WorkFlowSync.App.Views;

public partial class PairDialog : Window
{
    private readonly PairViewModel _draft;
    private readonly MainViewModel _main;
    private readonly PairViewModel? _editing;

    // Designer constructor
    public PairDialog() : this(new PairViewModel(), new MainViewModel(Path.GetTempFileName())) { }

    public PairDialog(PairViewModel draft, MainViewModel main, PairViewModel? editing = null)
    {
        _draft = draft;
        _main = main;
        _editing = editing;
        InitializeComponent();
        DataContext = draft;
        Title = editing is null ? I18n.T("pair.window_title") : $"{I18n.T("pair.window_title")} — {editing.Name}";
        HeaderText.Text = editing is null ? I18n.T("pair.title_new") : I18n.T("pair.title_edit", editing.Name);
    }

    private async void OnBrowseSource(object? sender, RoutedEventArgs e)
    {
        var picked = await PickFolderAsync(_draft.SourceLabel, _draft.Source);
        if (picked is null) return;
        _draft.Source = picked;
        if (string.IsNullOrWhiteSpace(_draft.Name))
            _draft.Name = _main.SuggestPairName(picked);
    }

    private async void OnBrowseTarget(object? sender, RoutedEventArgs e)
    {
        var picked = await PickFolderAsync(_draft.TargetLabel, _draft.Target);
        if (picked is not null) _draft.Target = picked;
    }

    private async Task<string?> PickFolderAsync(string title, string current)
    {
        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            start = await StorageProvider.TryGetFolderFromPathAsync(current);

        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        var folder = result.Count > 0 ? result[0] : null;
        return folder?.TryGetLocalPath();
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(_draft.Name)) problems.Add("Вкажіть назву завдання.");
        else if (_main.Pairs.Any(p => !ReferenceEquals(p, _editing) && string.Equals(p.Name, _draft.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            problems.Add($"Завдання з назвою «{_draft.Name.Trim()}» уже є.");
        if (string.IsNullOrWhiteSpace(_draft.Source)) problems.Add("Вкажіть вихідну теку.");
        if (string.IsNullOrWhiteSpace(_draft.Target)) problems.Add("Вкажіть кінцеву теку.");
        // Same rule as SyncConfig.Validate, so a pair rejected here is also rejected in a hand-edited config.
        if (SyncConfig.Nesting(_draft.Source, _draft.Target) is { } nesting)
            problems.Add(nesting.Contains("same folder") ? "Вихідна і кінцева тека — одна й та сама тека."
                : nesting.StartsWith("target", StringComparison.Ordinal) ? "Кінцева тека не може бути всередині вихідної."
                : "Вихідна тека не може бути всередині кінцевої.");
        if (_draft.HasMaxAge && _draft.MaxAgeDays < 1) problems.Add("Кількість днів для перенесення має бути ≥ 1.");
        if (_draft.HasAutoClean && _draft.AutoCleanDays < 1) problems.Add("Кількість днів для автоочищення має бути ≥ 1.");

        if (problems.Count > 0)
        {
            ErrorText.Text = string.Join(" ", problems);
            return;
        }
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
