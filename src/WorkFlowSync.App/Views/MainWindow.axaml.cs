using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WorkFlowSync.App.ViewModels;

namespace WorkFlowSync.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Keep the run log scrolled to the newest line.
        RunLog.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && RunLog.Text is { Length: > 0 } t)
                RunLog.CaretIndex = t.Length;
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    /// <summary>The pair a row button belongs to (row buttons carry the pair as their DataContext).</summary>
    private static PairViewModel? PairOf(object? sender) => (sender as Control)?.DataContext as PairViewModel;

    private async void OnAddPair(object? sender, RoutedEventArgs e)
    {
        var draft = new PairViewModel();
        var dialog = new PairDialog(draft, Vm);
        if (await dialog.ShowDialog<bool>(this))
            Vm.AddPair(draft);
    }

    private async void OnEditPair(object? sender, RoutedEventArgs e)
    {
        var pair = PairOf(sender) ?? Vm.SelectedPair;
        if (pair is null) return;
        var draft = pair.Clone();
        var dialog = new PairDialog(draft, Vm, editing: pair);
        if (await dialog.ShowDialog<bool>(this))
            Vm.ReplacePair(pair, draft);
    }

    private async void OnDeletePair(object? sender, RoutedEventArgs e)
    {
        if (PairOf(sender) is not { } pair) return;
        var ok = await ConfirmDialog.ShowAsync(this,
            "Видалити пару?",
            $"Пара «{pair.Name}» зникне зі списку. Уже скопійовані файли залишаться на місці, мережева папка не змінюється.",
            "Видалити");
        if (ok) Vm.RemovePair(pair);
    }

    private async void OnRunPair(object? sender, RoutedEventArgs e)
    {
        if (PairOf(sender) is not { } pair) return;
        await Vm.RunPairAsync(pair);
    }

    private async void OnTogglePair(object? sender, RoutedEventArgs e)
    {
        if (PairOf(sender) is not { } pair) return;
        await Vm.TogglePairAsync(pair);
    }

    private void OnOpenAppFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Vm.AppFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"Не вдалося відкрити папку: {ex.Message}";
            Vm.StatusIsError = true;
        }
    }

    private async void OnImportFfs(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Файл налаштувань FreeFileSync",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("FreeFileSync") { Patterns = new[] { "*.ffs_batch", "*.ffs_gui" } },
                FilePickerFileTypes.All,
            },
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null) return;
        try
        {
            Vm.ImportFreeFileSync(path);
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"Імпорт не вдався: {ex.Message}";
            Vm.StatusIsError = true;
        }
    }
}
