using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using WorkFlowSync.App.ViewModels;

namespace WorkFlowSync.App.Views;

public partial class ApprovalWindow : Window
{
    public ApprovalWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private ApprovalViewModel? Model => DataContext as ApprovalViewModel;

    private void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        Model?.Load();
    }

    private async void OnRowApproveClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ApprovalItemViewModel item && Model is not null)
        {
            await Model.ExecuteDecisionForItemsAsync(new[] { item }, ApprovalViewModel.DecisionKind.Approve);
        }
    }

    private async void OnRowDismissClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ApprovalItemViewModel item && Model is not null)
        {
            await Model.ExecuteDecisionForItemsAsync(new[] { item }, ApprovalViewModel.DecisionKind.Dismiss);
        }
    }

    private async void OnRowRevertClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ApprovalItemViewModel item && Model is not null)
        {
            await Model.ExecuteDecisionForItemsAsync(new[] { item }, ApprovalViewModel.DecisionKind.Revert);
        }
    }

    private async void OnApproveAllClicked(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        var items = Model.Items.Where(i => i.IsSelected).ToList();
        if (items.Count == 0) items = Model.FilteredItems.ToList();

        if (items.Count == 0) return;

        await Model.ExecuteDecisionForItemsAsync(items, ApprovalViewModel.DecisionKind.Approve);
    }

    private async void OnDismissAllClicked(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        var items = Model.Items.Where(i => i.IsSelected).ToList();
        if (items.Count == 0) items = Model.FilteredItems.ToList();

        if (items.Count == 0) return;

        await Model.ExecuteDecisionForItemsAsync(items, ApprovalViewModel.DecisionKind.Dismiss);
    }

    private async void OnRevertAllClicked(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        var items = Model.Items.Where(i => i.IsSelected).ToList();
        if (items.Count == 0) items = Model.FilteredItems.ToList();

        if (items.Count == 0) return;

        await Model.ExecuteDecisionForItemsAsync(items, ApprovalViewModel.DecisionKind.Revert);
    }

    private void OnSelectAllToggled(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && Model is not null)
        {
            Model.SelectAll(cb.IsChecked == true);
        }
    }
}
