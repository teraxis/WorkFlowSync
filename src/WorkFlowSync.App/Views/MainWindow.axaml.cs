using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

    private void OnRowDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        OnEditPair(sender, e);
    }

    private void OnOpenFolderPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is string path && !string.IsNullOrWhiteSpace(path))
        {
            try
            {
                if (System.IO.Directory.Exists(path) || System.IO.File.Exists(path))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                }
                else
                {
                    Vm.StatusText = $"Тека не існує: {path}";
                    Vm.StatusIsError = true;
                }
            }
            catch (Exception ex)
            {
                Vm.StatusText = $"Не вдалося відкрити теку: {ex.Message}";
                Vm.StatusIsError = true;
            }
            e.Handled = true;
        }
    }

    private async void OnDeletePair(object? sender, RoutedEventArgs e)
    {
        if (PairOf(sender) is not { } pair) return;
        var ok = await ConfirmDialog.ShowAsync(this,
            "Видалити завдання?",
            $"Завдання «{pair.Name}» зникне зі списку. Уже скопійовані файли залишаться на місці, теки не змінюються.",
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

    private void OnViewDetails(object? sender, RoutedEventArgs e)
    {
        Vm.SelectedPage = 1;
    }

    private void OnOpenApproval(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.ShowApprovalWindow();
        }
    }

    private void OnOpenAppFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Vm.AppFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"Не вдалося відкрити теку: {ex.Message}";
            Vm.StatusIsError = true;
        }
    }

}
