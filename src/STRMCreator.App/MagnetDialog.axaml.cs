using Avalonia.Controls;
using Avalonia.Interactivity;

namespace STRMCreator.App;

public partial class MagnetDialog : Window
{
    public MagnetDialog() => InitializeComponent();

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Accept_Click(object? sender, RoutedEventArgs e)
    {
        var value = MagnetTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(value))
            Close(value);
    }
}
