using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace STRMCreator.App;

public partial class NumberStepper : UserControl
{
    private readonly DispatcherTimer _repeatTimer;
    private bool _updatingText;
    private int _repeatDirection;

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
        _repeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _repeatTimer.Tick += RepeatTimer_Tick;
        UpdateText();
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
        {
            UpdateText();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Decrease_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        StartRepeat(-1, sender, e);

    private void Increase_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        StartRepeat(1, sender, e);

    private void StartRepeat(int direction, object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _repeatDirection = direction;
        ChangeValue(direction);
        _repeatTimer.Interval = TimeSpan.FromMilliseconds(420);
        _repeatTimer.Start();
        if (sender is InputElement element)
            e.Pointer.Capture(element);
        e.Handled = true;
    }

    private void RepeatTimer_Tick(object? sender, EventArgs e)
    {
        _repeatTimer.Interval = TimeSpan.FromMilliseconds(85);
        ChangeValue(_repeatDirection);
    }

    private void ChangeValue(int direction) =>
        Value = Math.Clamp(Value + direction, Minimum, Maximum);

    private void Button_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        StopRepeat();
        e.Pointer.Capture(null);
    }

    private void Button_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        StopRepeat();

    private void StopRepeat()
    {
        _repeatTimer.Stop();
        _repeatDirection = 0;
    }

    private void ValueBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updatingText) return;
        if (int.TryParse(ValueBox.Text, out var value))
            Value = Math.Clamp(value, Minimum, Maximum);
    }

    private void ValueBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down)
        {
            ChangeValue(e.Key == Key.Up ? 1 : -1);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            CommitText();
            e.Handled = true;
        }
    }

    private void ValueBox_LostFocus(object? sender, RoutedEventArgs e) => CommitText();

    private void CommitText()
    {
        if (int.TryParse(ValueBox.Text, out var value))
            Value = Math.Clamp(value, Minimum, Maximum);
        UpdateText();
    }

    private void UpdateText()
    {
        if (ValueBox is null) return;
        _updatingText = true;
        ValueBox.Text = Value.ToString();
        _updatingText = false;
    }
}
