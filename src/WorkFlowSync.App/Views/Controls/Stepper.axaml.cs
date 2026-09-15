using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace WorkFlowSync.App.Views.Controls;

/// <summary>Integer stepper: <see cref="Value"/> is clamped to [<see cref="Minimum"/>, <see cref="Maximum"/>].</summary>
public partial class Stepper : UserControl
{
    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<Stepper, int>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> MinimumProperty =
        AvaloniaProperty.Register<Stepper, int>(nameof(Minimum), 0);

    public static readonly StyledProperty<int> MaximumProperty =
        AvaloniaProperty.Register<Stepper, int>(nameof(Maximum), int.MaxValue);

    public static readonly StyledProperty<int> IncrementProperty =
        AvaloniaProperty.Register<Stepper, int>(nameof(Increment), 1);

    public int Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public int Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Increment { get => GetValue(IncrementProperty); set => SetValue(IncrementProperty, value); }

    private bool _syncing;

    public Stepper()
    {
        InitializeComponent();
        Box.Text = Value.ToString();
        Box.LostFocus += (_, _) => Commit();
        Box.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter: Commit(); e.Handled = true; break;
                case Key.Up: Step(+1); e.Handled = true; break;
                case Key.Down: Step(-1); e.Handled = true; break;
            }
        };
        Box.GotFocus += (_, _) => Frame.BorderBrush = this.FindResource("Accent") as Avalonia.Media.IBrush ?? Frame.BorderBrush;
        Box.LostFocus += (_, _) => Frame.BorderBrush = this.FindResource("BorderStrong") as Avalonia.Media.IBrush ?? Frame.BorderBrush;
        PointerWheelChanged += (_, e) =>
        {
            if (!Box.IsFocused) return;
            Step(e.Delta.Y > 0 ? +1 : -1);
            e.Handled = true;
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty && !_syncing)
        {
            var clamped = Clamp(Value);
            if (clamped != Value) { Value = clamped; return; }
            Box.Text = Value.ToString();
        }
        else if (change.Property == IsEnabledProperty)
        {
            Opacity = IsEnabled ? 1 : 0.55;
        }
    }

    private void OnUp(object? sender, RoutedEventArgs e) => Step(+1);
    private void OnDown(object? sender, RoutedEventArgs e) => Step(-1);

    private void Step(int direction)
    {
        Commit();
        Value = Clamp(Value + direction * Math.Max(1, Increment));
    }

    /// <summary>Parses the box; anything that is not a number falls back to the current value.</summary>
    private void Commit()
    {
        _syncing = true;
        try
        {
            if (int.TryParse((Box.Text ?? "").Trim(), out var n)) Value = Clamp(n);
            Box.Text = Value.ToString();
        }
        finally
        {
            _syncing = false;
        }
    }

    private int Clamp(int v) => Math.Min(Math.Max(v, Minimum), Maximum);
}
