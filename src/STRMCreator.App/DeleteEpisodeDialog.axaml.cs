using Avalonia.Controls;
using Avalonia.Interactivity;

namespace STRMCreator.App;

public partial class DeleteEpisodeDialog : Window
{
    public DeleteEpisodeDialog() => InitializeComponent();

    public DeleteEpisodeDialog(string title, int season, int episode) : this() =>
        EpisodeText.Text = $"{title} | season {season:00}, episode {episode:00}";

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Delete_Click(object? sender, RoutedEventArgs e) => Close(true);
}
