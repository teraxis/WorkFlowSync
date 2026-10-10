using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WorkFlowSync.App.ViewModels;

namespace WorkFlowSync.App.Views;

public partial class CloudFolderPickerDialog : Window
{
    public CloudFolderPickerDialog()
    {
        InitializeComponent();
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is CloudFolderPickerViewModel vm)
        {
            vm.RequestClose += () => Close(vm.Result);
            await vm.InitializeAsync();
        }
    }

    private void OnFolderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is CloudFolderPickerViewModel vm && vm.SelectedFolder is not null)
        {
            _ = vm.OpenFolderAsync(vm.SelectedFolder);
        }
    }
}
