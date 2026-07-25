using Avalonia.Controls;
using Avalonia.Interactivity;
using STRMshelf.App.Localization;

namespace STRMshelf.App;

public partial class RestoreStreamsDialog : Window
{
    public RestoreStreamsDialog() => InitializeComponent();

    public RestoreStreamsDialog(int count) : this() =>
        MessageText.Text = LocalizationManager.Format("MissingFilesCount", count);

    private void Later_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Restore_Click(object? sender, RoutedEventArgs e) => Close(true);
}
