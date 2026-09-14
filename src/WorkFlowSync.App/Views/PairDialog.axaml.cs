using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WorkFlowSync.App.ViewModels;

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
        Title = editing is null ? "Нова пара папок" : $"Пара папок — {editing.Name}";
        HeaderText.Text = editing is null ? "Нова пара" : $"Пара «{editing.Name}»";
    }

    private async void OnBrowseSource(object? sender, RoutedEventArgs e)
    {
        var picked = await PickFolderAsync("Мережева папка, за якою стежити", _draft.Source);
        if (picked is null) return;
        _draft.Source = picked;
        if (string.IsNullOrWhiteSpace(_draft.Name))
            _draft.Name = _main.SuggestPairName(picked);
    }

    private async void OnBrowseTarget(object? sender, RoutedEventArgs e)
    {
        var picked = await PickFolderAsync("Локальна папка для дзеркала (у OneDrive)", _draft.Target);
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
        if (string.IsNullOrWhiteSpace(_draft.Name)) problems.Add("Вкажіть назву пари.");
        else if (_main.Pairs.Any(p => !ReferenceEquals(p, _editing) && string.Equals(p.Name, _draft.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            problems.Add($"Пара з назвою «{_draft.Name.Trim()}» уже є.");
        if (string.IsNullOrWhiteSpace(_draft.Source)) problems.Add("Вкажіть мережеву папку (джерело).");
        if (string.IsNullOrWhiteSpace(_draft.Target)) problems.Add("Вкажіть локальну папку (дзеркало).");
        if (!string.IsNullOrWhiteSpace(_draft.Source) && !string.IsNullOrWhiteSpace(_draft.Target))
        {
            var s = Normalize(_draft.Source);
            var t = Normalize(_draft.Target);
            if (s.Equals(t, StringComparison.OrdinalIgnoreCase)) problems.Add("Джерело і дзеркало — одна й та сама папка.");
            else if (t.StartsWith(s, StringComparison.OrdinalIgnoreCase)) problems.Add("Дзеркало не може бути всередині джерела.");
            else if (s.StartsWith(t, StringComparison.OrdinalIgnoreCase)) problems.Add("Джерело не може бути всередині дзеркала.");
        }
        if (_draft.HasRetention && _draft.RetentionDays < 1) problems.Add("Кількість днів має бути ≥ 1.");

        if (problems.Count > 0)
        {
            ErrorText.Text = string.Join(" ", problems);
            return;
        }
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        return full.TrimEnd('\\', '/') + "\\";
    }
}
