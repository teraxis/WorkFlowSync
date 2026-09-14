using Avalonia.Controls;
using Avalonia.Interactivity;
using WorkFlowSync.App.ViewModels;

namespace WorkFlowSync.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    private async void OnAddPair(object? sender, RoutedEventArgs e)
    {
        var draft = new PairViewModel();
        var dialog = new PairDialog(draft, Vm);
        if (await dialog.ShowDialog<bool>(this))
            Vm.AddPair(draft);
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
