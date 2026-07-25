using Avalonia.Controls;
using Avalonia.Interactivity;
using STRMCreator.Core;
using STRMCreator.Infrastructure;

namespace STRMCreator.App;

public sealed record ReassignTorrentRequest(string InfoHash, IReadOnlyList<long> ItemIds,
    string TargetTitle, MediaKind TargetKind);

public partial class LinkedSourcesWindow : Window
{
    private LibraryDatabase? _database;
    private IReadOnlyList<LibraryItem> _items = [];
    private string _heading = "";
    private IReadOnlyList<SourceRow> _allRows = [];

    public LinkedSourcesWindow()
    {
        InitializeComponent();
    }

    public LinkedSourcesWindow(string title, IReadOnlyList<LibraryItem> items,
        LibraryDatabase database) : this()
    {
        _heading = title;
        _items = items;
        _database = database;
        Opened += async (_, _) => await LoadAsync();
    }

    public LinkedSourcesWindow(LibraryDatabase database) : this()
    {
        _heading = "All known torrent sources";
        _database = database;
        Opened += async (_, _) =>
        {
            _items = await database.GetLibraryAsync();
            await LoadAsync();
        };
    }

    private async Task LoadAsync()
    {
        TitleText.Text = _heading;
        var rows = new List<SourceRow>();
        foreach (var group in _items.GroupBy(item =>
                     (item.Kind, Title: Recognition.NormalizeTitle(item.Title), item.InfoHash)))
        {
            var item = group.First();
            var metadata = new TorrentParser().Parse(
                await _database!.GetTorrentDataAsync(item.InfoHash));
            var seasons = group.Where(value => value.SeasonNumber.HasValue)
                .Select(value => value.SeasonNumber!.Value).Distinct().Order().ToArray();
            var binding = item.Kind == MediaKind.Series ? $"TV show: {item.Title}" : $"Movie: {item.Title}";
            rows.Add(new SourceRow(metadata.Name, item.Title, item.InfoHash,
                group.Select(value => value.Id).ToArray(),
                $"{binding} | {metadata.Files.Count(file => file.IsVideo())} videos" +
                (seasons.Length > 0 ? $" | seasons {string.Join(", ", seasons)}" : ""),
                "Stored in database"));
        }
        _allRows = rows.OrderBy(row => row.Name).ToArray();
        ApplyFilter();
        await LoadTargetTitlesAsync();
    }

    private void SourceSearch_Changed(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SourceSearchBox.Text?.Trim() ?? "";
        var filtered = query.Length == 0
            ? _allRows
            : _allRows.Where(row =>
                row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                row.LinkedTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                row.Hash.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        SourcesList.ItemsSource = filtered;
        SourcesList.SelectedItem = filtered.FirstOrDefault();
    }

    private async void TargetKind_Changed(object? sender, RoutedEventArgs e) =>
        await LoadTargetTitlesAsync();

    private async Task LoadTargetTitlesAsync()
    {
        if (_database is null) return;
        var kind = TargetMovieRadio.IsChecked == true ? MediaKind.Movie : MediaKind.Series;
        TargetTitleBox.ItemsSource = (await _database.GetLibraryAsync())
            .Where(item => item.Kind == kind).Select(item => item.Title)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Order().ToArray();
    }

    private void Reassign_Click(object? sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is not SourceRow source) return;
        var title = TargetTitleBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return;
        var kind = TargetMovieRadio.IsChecked == true ? MediaKind.Movie : MediaKind.Series;
        Close(new ReassignTorrentRequest(source.Hash, source.ItemIds, title, kind));
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(null);

    private sealed record SourceRow(string Name, string LinkedTitle, string Hash,
        IReadOnlyList<long> ItemIds, string Detail, string Storage);
}
