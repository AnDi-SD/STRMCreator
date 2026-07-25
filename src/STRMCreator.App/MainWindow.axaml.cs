using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using STRMCreator.Core;
using STRMCreator.Infrastructure;

namespace STRMCreator.App;

public partial class MainWindow : Window
{
    private LibraryDatabase _database;
    private readonly BootstrapConfigStore _bootstrap = new();
    private readonly StreamSynchronizer _synchronizer = new();
    private readonly TorrentParser _torrentParser = new();
    private readonly MagnetMetadataService _magnetMetadata = new();
    private readonly ObservableCollection<EpisodeRow> _episodes = [];
    private readonly ObservableCollection<LibraryRow> _library = [];
    private TorrentMetadata? _torrent;
    private string? _torrentPath;
    private AppSettings _settings = AppSettings.Default;
    private List<SeriesChoice> _seriesChoices = [];

    public MainWindow()
    {
        InitializeComponent();
        _database = new LibraryDatabase(_bootstrap.DefaultDatabasePath);
        EpisodeList.ItemsSource = _episodes;
        LibraryList.ItemsSource = _library;
        Opened += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var bootstrap = await _bootstrap.LoadAsync();
            _database = new LibraryDatabase(bootstrap.DatabasePath);
            await _database.InitializeAsync();
            DatabasePathBox.Text = _database.DatabasePath;
            _settings = await _database.GetSettingsAsync();
            ServerUrlBox.Text = _settings.ServerUrl;
            MoviesPathBox.Text = _settings.MoviesPath;
            SeriesPathBox.Text = _settings.SeriesPath;
            await RefreshLibraryAsync();
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, true);
        }
    }

    private async void OpenTorrent_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите torrent-файл",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Torrent") { Patterns = ["*.torrent"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        await LoadTorrentAsync(path);
    }

    private async void OpenMagnet_Click(object? sender, RoutedEventArgs e)
    {
        var magnet = await new MagnetDialog().ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(magnet)) return;
        try
        {
            SetStatus("Получение metadata по magnet-ссылке. Это может занять несколько минут.");
            var directory = Path.Combine(Path.GetDirectoryName(_database.DatabasePath)!, "Torrents");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var path = await _magnetMetadata.DownloadAsync(magnet, directory, timeout.Token);
            await LoadTorrentAsync(path);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Получение magnet metadata отменено по тайм-ауту.", true);
        }
        catch (Exception exception)
        {
            SetStatus($"Не удалось получить magnet metadata: {exception.Message}", true);
        }
    }

    private async Task LoadTorrentAsync(string path)
    {
        try
        {
            _torrent = _torrentParser.Parse(path);
            _torrentPath = path;
            var suggested = Recognition.SuggestTitle(_torrent.Name);
            _seriesChoices = (await _database.FindSeriesAsync(suggested))
                .Select(x => new SeriesChoice(x.Id, x.Name, x.Score, x.Seasons)).ToList();
            TitleBox.ItemsSource = _seriesChoices;
            var confident = _seriesChoices.FirstOrDefault(x => x.Score >= 0.86);
            TitleBox.SelectedItem = confident;
            TitleBox.Text = confident?.Name ?? suggested;
            TorrentSummaryText.Text =
                $"{Path.GetFileName(path)} | {_torrent.Files.Count} файлов | " +
                $"{_torrent.Files.Count(x => x.IsVideo())} видео | {_torrent.InfoHash}";
            BuildEpisodePreview();
            SetStatus("Метаданные прочитаны. Проверьте название и нумерацию.");
        }
        catch (Exception exception)
        {
            SetStatus($"Не удалось прочитать torrent: {exception.Message}", true);
        }
    }

    private void BuildEpisodePreview()
    {
        if (_torrent is null) return;
        var title = CurrentTitle();
        var season = SeasonBox.Value;
        var first = FirstEpisodeBox.Value;
        var candidates = Recognition.DetectEpisodes(_torrent, season, first);
        _episodes.Clear();
        foreach (var candidate in candidates)
            _episodes.Add(new EpisodeRow(candidate.Source, candidate.SeasonNumber,
                candidate.EpisodeNumber, () => CurrentTitle()));
        EpisodeList.IsVisible = _episodes.Count > 0;
    }

    private async void ImportAndSync_Click(object? sender, RoutedEventArgs e)
    {
        if (_torrent is null || _torrentPath is null)
        {
            SetStatus("Сначала выберите torrent-файл.", true);
            return;
        }

        try
        {
            await SaveSettingsInternalAsync(validate: true);
            var kind = MovieKindRadio.IsChecked == true ? MediaKind.Movie : MediaKind.Series;
            var title = CurrentTitle();
            if (string.IsNullOrWhiteSpace(title))
                throw new InvalidOperationException("Укажите название.");
            var root = kind == MediaKind.Series ? _settings.SeriesPath : _settings.MoviesPath;
            var outputDirectory = OutputPath.SanitizeSegment(title);
            long? seriesId = null;
            int? season = kind == MediaKind.Series ? SeasonBox.Value : null;
            if (kind == MediaKind.Series)
                seriesId = await _database.GetOrCreateSeriesAsync(title, _torrent.Name);

            var created = 0;
            var updated = 0;
            var deleted = 0;
            var unchanged = 0;
            var groups = kind == MediaKind.Movie
                ? new[] { _episodes.AsEnumerable() }
                : _episodes.Where(x => x.Selected).GroupBy(x => x.Season).Select(x => x.AsEnumerable());
            foreach (var group in groups)
            {
                var rows = group.ToArray();
                var groupSeason = kind == MediaKind.Series ? rows.First().Season : season;
                var itemId = await _database.UpsertLibraryItemAsync(kind, seriesId, title, _torrentPath,
                    _torrent.InfoHash, groupSeason, outputDirectory);
                var previous = await _database.GetStreamsAsync(itemId);
                var streams = BuildStreams(itemId, kind, title, outputDirectory, rows);
                var plan = await _synchronizer.PlanAsync(root, streams, previous);
                await _synchronizer.ApplyAsync(root, plan);
                await _database.ReplaceStreamsAsync(itemId, streams);
                created += plan.Create.Count;
                updated += plan.Update.Count;
                deleted += plan.Delete.Count;
                unchanged += plan.Unchanged.Count;
            }
            await RefreshLibraryAsync();
            SetStatus($"Готово: создано {created}, обновлено {updated}, " +
                      $"удалено {deleted}, без изменений {unchanged}.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, true);
        }
    }

    private IReadOnlyList<ManagedStream> BuildStreams(long itemId, MediaKind kind, string title,
        string directory, IReadOnlyList<EpisodeRow>? rows = null)
    {
        if (_torrent is null) return [];
        if (kind == MediaKind.Movie)
        {
            var video = _torrent.Files.Where(x => x.IsVideo()).OrderByDescending(x => x.Length).FirstOrDefault()
                        ?? throw new InvalidOperationException("В torrent нет поддерживаемых видеофайлов.");
            return [new ManagedStream(0, itemId, video.Index, video.Path,
                $"{directory}/{OutputPath.SanitizeSegment(title)}.strm",
                StreamUrlBuilder.Build(_settings.ServerUrl, _torrent.InfoHash, video))];
        }
        return (rows ?? _episodes).Where(x => x.Selected).Select(x =>
            new ManagedStream(0, itemId, x.Source.Index, x.Source.Path,
                $"{directory}/{OutputPath.SanitizeSegment(x.OutputName)}",
                StreamUrlBuilder.Build(_settings.ServerUrl, _torrent.InfoHash, x.Source))).ToArray();
    }

    private async void SaveSettings_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsInternalAsync(validate: true);
            SetStatus("Настройки сохранены, каталоги доступны для записи.");
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private async Task SaveSettingsInternalAsync(bool validate)
    {
        var settings = new AppSettings(ServerUrlBox.Text?.Trim() ?? "",
            MoviesPathBox.Text?.Trim() ?? "", SeriesPathBox.Text?.Trim() ?? "");
        if (!Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("Укажите корректный абсолютный адрес TorrServer.");
        if (validate)
        {
            if (!string.IsNullOrWhiteSpace(settings.MoviesPath))
                await _synchronizer.ValidateWritableAsync(settings.MoviesPath);
            if (!string.IsNullOrWhiteSpace(settings.SeriesPath))
                await _synchronizer.ValidateWritableAsync(settings.SeriesPath);
        }
        await _database.SaveSettingsAsync(settings);
        _settings = settings;
    }

    private async void SelectDatabase_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите существующую базу",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SQLite database") { Patterns = ["*.db"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        await SwitchDatabaseAsync(path);
    }

    private async void CreateDatabase_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickDatabaseSavePathAsync("Создать новую базу");
        if (path is not null)
            await SwitchDatabaseAsync(path);
    }

    private async void MoveDatabase_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = await PickDatabaseSavePathAsync("Перенести текущую базу");
            if (path is null) return;
            await _database.BackupAsync(path);
            await SwitchDatabaseAsync(path);
            SetStatus("База перенесена, новый файл выбран как активный.");
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private async void BackupDatabase_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = await PickDatabaseSavePathAsync("Резервная копия базы");
            if (path is null) return;
            await _database.BackupAsync(path);
            SetStatus($"Резервная копия создана: {path}");
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private async Task<string?> PickDatabaseSavePathAsync(string title)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = "library.db",
            DefaultExtension = "db",
            FileTypeChoices = [new FilePickerFileType("SQLite database") { Patterns = ["*.db"] }]
        });
        return file?.TryGetLocalPath();
    }

    private async Task SwitchDatabaseAsync(string path)
    {
        try
        {
            var database = new LibraryDatabase(path);
            await database.InitializeAsync();
            _database = database;
            await _bootstrap.SaveAsync(database.DatabasePath);
            DatabasePathBox.Text = database.DatabasePath;
            _settings = await database.GetSettingsAsync();
            ServerUrlBox.Text = _settings.ServerUrl;
            MoviesPathBox.Text = _settings.MoviesPath;
            SeriesPathBox.Text = _settings.SeriesPath;
            await RefreshLibraryAsync();
            SetStatus("База данных подключена.");
        }
        catch (Exception exception) { SetStatus($"Не удалось открыть базу: {exception.Message}", true); }
    }

    private async void BrowseMovies_Click(object? sender, RoutedEventArgs e) =>
        MoviesPathBox.Text = await PickFolderAsync("Каталог фильмов") ?? MoviesPathBox.Text;

    private async void BrowseSeries_Click(object? sender, RoutedEventArgs e) =>
        SeriesPathBox.Text = await PickFolderAsync("Каталог сериалов") ?? SeriesPathBox.Text;

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task RefreshLibraryAsync()
    {
        var items = await _database.GetLibraryAsync();
        _library.Clear();
        foreach (var item in items)
            _library.Add(new LibraryRow(item));
        LibraryCountText.Text = $"{items.Count} источников";
    }

    private void Numbering_Changed(object? sender, EventArgs e) => BuildEpisodePreview();
    private void MediaKind_Changed(object? sender, RoutedEventArgs e)
    {
        var series = SeriesKindRadio?.IsChecked == true;
        if (SeasonBox is not null) SeasonBox.IsEnabled = series;
        if (FirstEpisodeBox is not null) FirstEpisodeBox.IsEnabled = series;
        BuildEpisodePreview();
    }

    private void TitleBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TitleBox.SelectedItem is SeriesChoice choice)
        {
            TitleBox.Text = choice.Name;
            if (choice.Seasons.Count > 0 && SeasonBox.Value == 1)
                SeasonBox.Value = choice.Seasons.Max() + 1;
        }
        foreach (var row in _episodes) row.NotifyOutputChanged();
    }

    private void SettingsToggle_Click(object? sender, RoutedEventArgs e) =>
        SettingsPanel.IsVisible = !SettingsPanel.IsVisible;

    private void LibraryList_SelectionChanged(object? sender, SelectionChangedEventArgs e) { }

    private async void SyncAll_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsInternalAsync(validate: true);
            var created = 0;
            var updated = 0;
            foreach (var item in await _database.GetLibraryAsync())
            {
                var streams = await _database.GetStreamsAsync(item.Id);
                var root = item.Kind == MediaKind.Series ? _settings.SeriesPath : _settings.MoviesPath;
                var revised = streams.Select(x => x with
                {
                    Content = RebaseServer(x.Content, _settings.ServerUrl)
                }).ToArray();
                var plan = await _synchronizer.PlanAsync(root, revised, streams);
                await _synchronizer.ApplyAsync(root, plan);
                await _database.ReplaceStreamsAsync(item.Id, revised);
                created += plan.Create.Count;
                updated += plan.Update.Count;
            }
            SetStatus($"Медиатека синхронизирована: создано {created}, обновлено {updated}.");
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private static string RebaseServer(string content, string serverUrl)
    {
        if (!Uri.TryCreate(content, UriKind.Absolute, out var old)) return content;
        var suffix = old.PathAndQuery;
        return serverUrl.TrimEnd('/') + suffix;
    }

    private string CurrentTitle() => TitleBox.Text?.Trim() ?? "";
    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = Avalonia.Media.Brushes.Red;
        if (!error) StatusText.Foreground = Avalonia.Media.Brushes.DimGray;
    }

    private sealed record SeriesChoice(long Id, string Name, double Score, IReadOnlyList<int> Seasons)
    {
        public override string ToString() =>
            Seasons.Count == 0 ? Name : $"{Name} (сезоны: {string.Join(", ", Seasons)})";
    }

    private sealed record LibraryRow(LibraryItem Item)
    {
        public string Title => Item.Title;
        public string Detail => Item.Kind == MediaKind.Series
            ? $"Сериал | сезон {Item.SeasonNumber:00}" : "Фильм";
        public string Status => Item.UpdatedAt.LocalDateTime.ToString("dd.MM.yyyy");
    }

    private sealed class EpisodeRow : INotifyPropertyChanged
    {
        private int _season;
        private int _episode;
        private bool _selected = true;
        private readonly Func<string> _title;
        public EpisodeRow(TorrentFile source, int season, int episode, Func<string> title) =>
            (Source, _season, _episode, _title) = (source, season, episode, title);
        public TorrentFile Source { get; }
        public string SourceName => Source.Name;
        public int Index => Source.Index;
        public int Season { get => _season; set { _season = value; Notify(); Notify(nameof(OutputName)); } }
        public int Episode { get => _episode; set { _episode = value; Notify(); Notify(nameof(OutputName)); } }
        public bool Selected { get => _selected; set { _selected = value; Notify(); } }
        public string OutputName => $"{OutputPath.SanitizeSegment(_title())} s{Season:00}e{Episode:00}.strm";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void NotifyOutputChanged() => Notify(nameof(OutputName));
        private void Notify([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
