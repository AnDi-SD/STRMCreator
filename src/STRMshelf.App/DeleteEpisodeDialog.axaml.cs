using Avalonia.Controls;
using Avalonia.Interactivity;
using STRMshelf.App.Localization;

namespace STRMshelf.App;

public partial class DeleteEpisodeDialog : Window
{
    public DeleteEpisodeDialog() => InitializeComponent();

    public DeleteEpisodeDialog(string title, int season, int episode) : this() =>
        EpisodeText.Text = LocalizationManager.Format("EpisodeIdentity", title, season, episode);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Delete_Click(object? sender, RoutedEventArgs e) => Close(true);
}
