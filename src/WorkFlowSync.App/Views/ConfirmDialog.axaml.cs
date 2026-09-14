using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WorkFlowSync.App.Views;

/// <summary>Small yes/no dialog in the app's own style (Avalonia has no built-in message box).</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public static async Task<bool> ShowAsync(Window owner, string title, string body, string confirmText)
    {
        var dialog = new ConfirmDialog { Title = title };
        dialog.TitleText.Text = title;
        dialog.BodyText.Text = body;
        dialog.ConfirmButton.Content = confirmText;
        return await dialog.ShowDialog<bool>(owner);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
