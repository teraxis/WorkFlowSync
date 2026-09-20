using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using WorkFlowSync.Core.Notifications;

namespace WorkFlowSync.App.Views;

/// <summary>
/// A short message in the corner of the screen (docs/plan-etap5.md §5.6b).
///
/// Why not a real Windows balloon: <c>Shell_NotifyIcon</c> with <c>NIF_INFO</c> needs to own the tray icon,
/// and Avalonia owns ours. Taking it over by hand would mean rewriting working tray code for a cosmetic
/// gain. The WinRT toast API is the other option and needs an AppUserModelID plus a Start-menu shortcut —
/// i.e. an installation, which this product deliberately does not have.
///
/// What is given up: these do not appear in the Windows notification centre and do not observe Focus
/// Assist. What is kept: the program stays portable, and the window obeys the app's own theme.
/// </summary>
public partial class NotificationWindow : Window
{
    /// <summary>Long enough to read two lines, short enough not to sit in the way.</summary>
    private static readonly TimeSpan Visible = TimeSpan.FromSeconds(7);

    private readonly DispatcherTimer _hide;

    /// <summary>Raised when the user clicks the message. (Not «Activated» — that is Window's own event.)</summary>
    public event Action? Clicked;

    public NotificationWindow()
    {
        InitializeComponent();
        _hide = new DispatcherTimer { Interval = Visible };
        _hide.Tick += (_, _) => Dismiss();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Shows the message in the corner. Never takes focus — the user is in the middle of something.</summary>
    public void Show(Notification note)
    {
        this.FindControl<TextBlock>("TitleText")!.Text = note.Title;
        this.FindControl<TextBlock>("BodyText")!.Text = note.Body;

        var app = Application.Current;
        if (app is not null && app.TryGetResource(note.IsProblem ? "Danger" : "Accent", app.ActualThemeVariant, out var brush)
            && brush is IBrush stripeBrush)
            this.FindControl<Border>("Accent")!.Background = stripeBrush;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            const int margin = 12;
            Position = new Avalonia.PixelPoint(
                area.X + Math.Max(0, area.Width - (int)(Width * scale) - (int)(margin * scale)),
                area.Y + Math.Max(0, area.Height - (int)(Height * scale) - (int)(margin * scale)));
        }

        base.Show();
        _hide.Stop();
        _hide.Start();      // restarted on every message, so a second one does not inherit a spent timer
    }

    private void Dismiss()
    {
        _hide.Stop();
        Hide();
    }

    private void OnClicked(object? sender, PointerPressedEventArgs e)
    {
        Dismiss();
        Clicked?.Invoke();
    }
}
