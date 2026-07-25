using Avalonia.Controls;
using Avalonia.Interactivity;

namespace STRMCreator.App;

public partial class DeleteLibraryItemDialog : Window
{
    public DeleteLibraryItemDialog() => InitializeComponent();

    public DeleteLibraryItemDialog(string title) : this() => ItemTitleText.Text = title;

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Delete_Click(object? sender, RoutedEventArgs e) => Close(true);
}
