using Avalonia.Controls;
using Avalonia.Interactivity;

namespace STRMCreator.App;

public partial class SyncSettingsDialog : Window
{
    public SyncSettingsDialog() => InitializeComponent();

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
    private void Later_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Sync_Click(object? sender, RoutedEventArgs e) => Close(true);
}
