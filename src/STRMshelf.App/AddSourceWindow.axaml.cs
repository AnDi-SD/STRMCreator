using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using STRMshelf.App.Localization;
using STRMshelf.Core;
using STRMshelf.Infrastructure;

namespace STRMshelf.App;

public sealed record AddSourceFile(TorrentFile Source, int Season, int Episode, bool Selected);
public sealed record AddSourceResult(byte[] TorrentData, string? MagnetUri, TorrentMetadata Torrent,
    MediaKind Kind, string Title, IReadOnlyList<AddSourceFile> Files);

public partial class AddSourceWindow : Window
{
    private readonly LibraryDatabase _database;
    private readonly TorrentParser _parser = new();
    private readonly MagnetMetadataService _magnetMetadata = new();
    private readonly ObservableCollection<SourceFileRow> _files = [];
    private TorrentMetadata? _torrent;
    private byte[]? _torrentData;
    private string? _magnetUri;
    private bool _updating;

    public AddSourceWindow()
    {
        InitializeComponent();
        _database = null!;
        FileList.ItemsSource = _files;
        TitleBox.PropertyChanged += (_, args) =>
        {
            if (args.Property == ComboBox.TextProperty)
                foreach (var row in _files) row.NotifyOutputChanged();
        };
    }

    public AddSourceWindow(LibraryDatabase database) : this() => _database = database;

    private async void OpenTorrent_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationManager.Get("SelectTorrentFile"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Torrent") { Patterns = ["*.torrent"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
            await LoadTorrentAsync(await File.ReadAllBytesAsync(path), Path.GetFileName(path), null);
    }

    private async void OpenMagnet_Click(object? sender, RoutedEventArgs e)
    {
        var magnet = await new MagnetDialog().ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(magnet)) return;
        try
        {
            SetStatus(LocalizationManager.Get("RetrievingMagnetMetadata"));
            var directory = Path.Combine(Path.GetTempPath(), "STRMshelf", "metadata-cache");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var data = await _magnetMetadata.DownloadDataAsync(magnet, directory, timeout.Token);
            await LoadTorrentAsync(data, "Magnet", magnet);
        }
        catch (OperationCanceledException)
        {
            SetStatus(LocalizationManager.Get("MetadataTimedOut"), true);
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private async Task LoadTorrentAsync(byte[] data, string sourceName, string? magnetUri)
    {
        try
        {
            _updating = true;
            _torrent = _parser.Parse(data);
            _torrentData = data;
            _magnetUri = magnetUri;
            if (!_torrent.Files.Any(file => file.IsVideo()))
                throw new InvalidOperationException(LocalizationManager.Get("NoSupportedVideo"));
            var kind = Recognition.DetectMediaKind(_torrent);
            SeriesKindRadio.IsChecked = kind == MediaKind.Series;
            MovieKindRadio.IsChecked = kind == MediaKind.Movie;
            TitleBox.Text = Recognition.SuggestTitle(_torrent.Name);
            await LoadTitleSuggestionsAsync(kind);
            RebuildFiles();
            SourceSummaryText.Text =
                $"{sourceName} | {LocalizationManager.Format("VideoCount", _torrent.Files.Count(x => x.IsVideo()))} | {_torrent.InfoHash}";
            EmptySourcePanel.IsVisible = false;
            MetadataPanel.IsVisible = true;
            FileHeaderPanel.IsVisible = true;
            FileList.IsVisible = true;
            ActionPanel.IsVisible = true;
            AddButton.IsEnabled = true;
            SetStatus(LocalizationManager.Get(kind == MediaKind.Movie
                ? "MovieDetected"
                : "SeriesDetected"));
        }
        catch (Exception exception)
        {
            SetStatus(LocalizationManager.Format("CouldNotReadSource", exception.Message), true);
        }
        finally { _updating = false; }
    }

    private async Task LoadTitleSuggestionsAsync(MediaKind kind)
    {
        var titles = (await _database.GetLibraryAsync())
            .Where(item => item.Kind == kind)
            .Select(item => item.Title)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order()
            .ToArray();
        TitleBox.ItemsSource = titles;
    }

    private async void MediaKind_Changed(object? sender, RoutedEventArgs e)
    {
        if (_updating || _torrent is null) return;
        _updating = true;
        try
        {
            await LoadTitleSuggestionsAsync(CurrentKind());
            RebuildFiles();
        }
        finally { _updating = false; }
    }

    private void RebuildFiles()
    {
        if (_torrent is null) return;
        var isSeries = CurrentKind() == MediaKind.Series;
        SeasonHeader.IsVisible = isSeries;
        EpisodeHeader.IsVisible = isSeries;
        var candidates = Recognition.DetectEpisodes(_torrent);
        var visible = isSeries ? candidates : candidates.OrderByDescending(x => x.Source.Length).Take(1);
        _files.Clear();
        foreach (var candidate in visible)
            _files.Add(new SourceFileRow(candidate.Source, candidate.SeasonNumber,
                candidate.EpisodeNumber, () => TitleBox.Text?.Trim() ?? "", isSeries));
    }

    private MediaKind CurrentKind() =>
        MovieKindRadio.IsChecked == true ? MediaKind.Movie : MediaKind.Series;

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        if (_torrent is null || _torrentData is null) return;
        var title = TitleBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            SetStatus(LocalizationManager.Get("EnterTitle"), true);
            return;
        }
        var selected = _files.Where(row => row.Selected)
            .Select(row => new AddSourceFile(row.Source, row.Season, row.Episode, true)).ToArray();
        if (selected.Length == 0)
        {
            SetStatus(LocalizationManager.Get("SelectVideo"), true);
            return;
        }
        Close(new AddSourceResult(_torrentData, _magnetUri, _torrent, CurrentKind(), title, selected));
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? Avalonia.Media.Brushes.Red : Avalonia.Media.Brushes.DimGray;
        EmptyStatusText.Text = text;
        EmptyStatusText.Foreground = StatusText.Foreground;
    }

    private sealed class SourceFileRow : INotifyPropertyChanged
    {
        private int _season;
        private int _episode;
        private bool _selected = true;
        private readonly Func<string> _title;

        public SourceFileRow(TorrentFile source, int season, int episode, Func<string> title, bool isSeries) =>
            (Source, _season, _episode, _title, IsSeries) = (source, season, episode, title, isSeries);

        public TorrentFile Source { get; }
        public string SourceName => Source.Name;
        public int Index => Source.Index;
        public bool IsSeries { get; }
        public int Season { get => _season; set { _season = value; Notify(); Notify(nameof(OutputName)); } }
        public int Episode { get => _episode; set { _episode = value; Notify(); Notify(nameof(OutputName)); } }
        public bool Selected { get => _selected; set { _selected = value; Notify(); } }
        public string OutputName => IsSeries
            ? $"{OutputPath.SanitizeSegment(_title())} s{Season:00}e{Episode:00}.strm"
            : $"{OutputPath.SanitizeSegment(_title())}.strm";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void NotifyOutputChanged() => Notify(nameof(OutputName));
        private void Notify([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
