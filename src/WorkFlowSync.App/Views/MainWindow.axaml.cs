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

    private async void OnAddPair(object? sender, RoutedEventArgs e)
    {
        var draft = new PairViewModel();
        var dialog = new PairDialog(draft, Vm);
        if (await dialog.ShowDialog<bool>(this))
            Vm.AddPair(draft);
    }

    private async void OnImportFfs(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Файл налаштувань FreeFileSync",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("FreeFileSync") { Patterns = new[] { "*.ffs_batch", "*.ffs_gui" } },
                Avalonia.Platform.Storage.FilePickerFileTypes.All,
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

    private async void OnEditPair(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedPair is not { } current) return;
        var draft = current.Clone();
        var dialog = new PairDialog(draft, Vm, editing: current);
        if (await dialog.ShowDialog<bool>(this))
            Vm.ReplacePair(current, draft);
    }
}
