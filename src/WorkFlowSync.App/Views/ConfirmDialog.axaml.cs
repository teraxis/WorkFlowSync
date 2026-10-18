using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WorkFlowSync.App.Views;

public sealed record ConfirmDialogResult(bool Confirmed, bool CheckboxChecked);

/// <summary>Small yes/no dialog in the app's own style (Avalonia has no built-in message box).</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public static async Task<bool> ShowAsync(Window owner, string title, string body, string confirmText)
    {
        var res = await ShowDetailedAsync(owner, title, body, confirmText);
        return res.Confirmed;
    }

    public static async Task<ConfirmDialogResult> ShowDetailedAsync(
        Window owner,
        string title,
        string body,
        string confirmText,
        string? checkboxText = null,
        bool defaultChecked = false)
    {
        var dialog = new ConfirmDialog { Title = title };
        dialog.TitleText.Text = title;
        dialog.BodyText.Text = body;
        dialog.ConfirmButton.Content = confirmText;
        if (!string.IsNullOrEmpty(checkboxText))
        {
            dialog.ApplyToExistingCheckBox.Content = checkboxText;
            dialog.ApplyToExistingCheckBox.IsChecked = defaultChecked;
            dialog.ApplyToExistingCheckBox.IsVisible = true;
        }
        var confirmed = await dialog.ShowDialog<bool>(owner);
        return new ConfirmDialogResult(confirmed, dialog.ApplyToExistingCheckBox.IsChecked == true);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
