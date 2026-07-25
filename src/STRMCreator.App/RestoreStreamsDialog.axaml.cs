using Avalonia.Controls;
using Avalonia.Interactivity;

namespace STRMCreator.App;

public partial class RestoreStreamsDialog : Window
{
    public RestoreStreamsDialog() => InitializeComponent();

    public RestoreStreamsDialog(int count) : this() =>
        MessageText.Text = $"{count} files are missing. Restore them now?";

    private void Later_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Restore_Click(object? sender, RoutedEventArgs e) => Close(true);
}
