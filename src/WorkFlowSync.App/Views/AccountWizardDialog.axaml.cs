using Avalonia.Controls;
using Avalonia.Interactivity;
using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.App.Views;

public partial class AccountWizardDialog : Window
{
    public AccountWizardViewModel Vm => (AccountWizardViewModel)DataContext!;

    public AccountWizardDialog()
    {
        InitializeComponent();
    }

    public static async Task<CloudAccount?> ShowAsync(Window owner, string? clientId = null, string? clientSecret = null)
    {
        var vm = new AccountWizardViewModel(clientId, clientSecret);
        var dialog = new AccountWizardDialog { DataContext = vm };
        var res = await dialog.ShowDialog<bool>(owner);
        return res ? vm.ResultAccount : null;
    }

    private void OnDone(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Vm.CancelAuthorization();
        Close(false);
    }

    private void OnSelectProviderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CloudProviderOption provider })
        {
            Vm.SelectProvider(provider);
        }
    }
}
