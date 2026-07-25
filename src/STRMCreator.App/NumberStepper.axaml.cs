using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace STRMCreator.App;

public partial class NumberStepper : UserControl
{
    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<NumberStepper, int>(
            nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> MinimumProperty =
        AvaloniaProperty.Register<NumberStepper, int>(nameof(Minimum), 0);

    public static readonly StyledProperty<int> MaximumProperty =
        AvaloniaProperty.Register<NumberStepper, int>(nameof(Maximum), 9999);

    public NumberStepper()
    {
        InitializeComponent();
    }

    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, Minimum, Maximum));
    }

    public int Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public event EventHandler? ValueChanged;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
            ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Decrease_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        Value = Math.Max(Minimum, Value - 1);

    private void Increase_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        Value = Math.Min(Maximum, Value + 1);
}
