using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using STRMshelf.Core;
using STRMshelf.Infrastructure;

namespace STRMshelf.App;

public sealed record MediaFileMapping(LibraryItem SourceItem, TorrentFile File,
    bool Selected, int Season, int Episode);

public partial class MediaFilesWindow : Window
{
    private readonly LibraryDatabase? _database;
    private readonly IReadOnlyList<LibraryItem> _items = [];
    private readonly ObservableCollection<MappingRow> _rows = [];
    private bool IsSeries => _items.FirstOrDefault()?.Kind == MediaKind.Series;

    public MediaFilesWindow()
    {
        InitializeComponent();
        FilesList.ItemsSource = _rows;
    }

    public MediaFilesWindow(string title, IReadOnlyList<LibraryItem> items,
        LibraryDatabase database) : this()
    {
        TitleText.Text = title;
        _items = items;
        _database = database;
        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        SeasonHeader.IsVisible = IsSeries;
        EpisodeHeader.IsVisible = IsSeries;
        foreach (var sourceGroup in _items.GroupBy(item => item.InfoHash))
        {
            var sourceItem = sourceGroup.First();
            var metadata = new TorrentParser().Parse(
                await _database!.GetTorrentDataAsync(sourceItem.InfoHash));
            var stored = new Dictionary<int, (ManagedStream Stream, LibraryItem Item)>();
            foreach (var item in sourceGroup)
                foreach (var stream in await _database.GetStreamsAsync(item.Id))
                    stored[stream.TorrentIndex] = (stream, item);

            foreach (var file in metadata.Files.Where(file => file.IsVideo()))
            {
                var selected = stored.TryGetValue(file.Index, out var value);
                var detected = Recognition.DetectEpisodes(
                    new TorrentMetadata(metadata.Name, metadata.InfoHash, [file])).Single();
                var (season, episode) = selected
                    ? ParseMapping(value.Stream.RelativePath,
                        value.Item.SeasonNumber ?? detected.SeasonNumber, detected.EpisodeNumber)
                    : (detected.SeasonNumber, detected.EpisodeNumber);
                _rows.Add(new MappingRow(sourceItem, metadata.Name, file, selected, season, episode,
                    IsSeries));
            }
        }
    }

    private static (int Season, int Episode) ParseMapping(string path, int season, int episode)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(path),
            @"\bs(?<season>\d{1,3})e(?<episode>\d{1,4})\b", RegexOptions.IgnoreCase);
        return match.Success
            ? (int.Parse(match.Groups["season"].Value), int.Parse(match.Groups["episode"].Value))
            : (season, episode);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(row => row.Selected).ToArray();
        if (_items[0].Kind == MediaKind.Movie && selected.Length != 1)
        {
            StatusText.Text = "Exactly one video file must be selected for a movie.";
            StatusText.Foreground = Avalonia.Media.Brushes.Red;
            return;
        }
        var duplicate = selected.GroupBy(row => (row.Season, row.Episode))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            StatusText.Text = $"Season {duplicate.Key.Season}, episode {duplicate.Key.Episode} is assigned more than once.";
            StatusText.Foreground = Avalonia.Media.Brushes.Red;
            return;
        }
        Close(_rows.Select(row => new MediaFileMapping(row.SourceItem, row.File,
            row.Selected, row.Season, row.Episode)).ToArray());
    }

    private sealed class MappingRow(LibraryItem sourceItem, string sourceName, TorrentFile file,
        bool selected, int season, int episode, bool isSeries)
    {
        public LibraryItem SourceItem { get; } = sourceItem;
        public TorrentFile File { get; } = file;
        public string FileName => File.Name;
        public string SourceName { get; } = sourceName;
        public int Index => File.Index;
        public bool IsSeries { get; } = isSeries;
        public bool Selected { get; set; } = selected;
        public int Season { get; set; } = season;
        public int Episode { get; set; } = episode;
    }
}
