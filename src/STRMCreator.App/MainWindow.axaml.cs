using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
    private readonly List<LibraryRow> _allLibrary = [];
    private TorrentMetadata? _torrent;
    private string? _torrentPath;
    private AppSettings _settings = AppSettings.Default;
    private List<SeriesChoice> _seriesChoices = [];
    private LibraryItem? _editingItem;
    private bool _loadingEditor;
    private bool _editorDirty;
    private EditorSnapshot? _editorSnapshot;
    private MediaKind? _libraryFilter;

    public MainWindow()
    {
        InitializeComponent();
        _database = new LibraryDatabase(_bootstrap.DefaultDatabasePath);
        EpisodeList.ItemsSource = _episodes;
        LibraryList.ItemsSource = _library;
        TitleBox.PropertyChanged += (_, args) =>
        {
            if (args.Property == ComboBox.TextProperty)
                MarkEditorDirty();
        };
        Opened += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var bootstrap = await _bootstrap.LoadAsync();
            _database = new LibraryDatabase(bootstrap.DatabasePath);
            await _database.InitializeAsync();
            _settings = await _database.GetSettingsAsync();
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
            ResetEditor();
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
            ImportButton.IsVisible = true;
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
        var isSeries = SeriesKindRadio.IsChecked == true;
        var title = CurrentTitle();
        var season = SeasonBox.Value;
        var first = FirstEpisodeBox.Value;
        var candidates = Recognition.DetectEpisodes(_torrent, season, first);
        var previewCandidates = isSeries
            ? candidates
            : candidates.OrderByDescending(x => x.Source.Length).Take(1);
        _episodes.Clear();
        foreach (var candidate in previewCandidates)
            AddEpisodeRow(new EpisodeRow(candidate.Source, candidate.SeasonNumber,
                candidate.EpisodeNumber, () => CurrentTitle(), isSeries));
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
            await SaveSettingsAsync(_settings, validate: true);
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

            if (_editingItem is not null)
            {
                await SaveEditedItemAsync(kind, seriesId, title, outputDirectory);
                return;
            }

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
            ResetEditor();
            SetStatus($"Готово: создано {created}, обновлено {updated}, " +
                      $"удалено {deleted}, без изменений {unchanged}.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, true);
        }
    }

    private async Task SaveEditedItemAsync(MediaKind kind, long? seriesId, string title,
        string outputDirectory)
    {
        var item = _editingItem!;
        var selected = _episodes.Where(x => x.Selected).ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException("Выберите хотя бы один видеофайл.");
        var seasons = selected.Select(x => x.Season).Distinct().ToArray();
        if (kind == MediaKind.Series && seasons.Length != 1)
            throw new InvalidOperationException("Одна запись медиатеки должна содержать серии одного сезона.");

        var season = kind == MediaKind.Series ? seasons[0] : (int?)null;
        var previous = await _database.GetStreamsAsync(item.Id);
        var streams = BuildStreams(item.Id, kind, title, outputDirectory, selected);
        var oldRoot = item.Kind == MediaKind.Series ? _settings.SeriesPath : _settings.MoviesPath;
        var newRoot = kind == MediaKind.Series ? _settings.SeriesPath : _settings.MoviesPath;

        SyncPlan plan;
        if (string.Equals(Path.GetFullPath(oldRoot), Path.GetFullPath(newRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            plan = await _synchronizer.PlanAsync(newRoot, streams, previous);
            await _synchronizer.ApplyAsync(newRoot, plan);
        }
        else
        {
            var removal = await _synchronizer.PlanAsync(oldRoot, [], previous);
            await _synchronizer.ApplyAsync(oldRoot, removal);
            plan = await _synchronizer.PlanAsync(newRoot, streams, []);
            await _synchronizer.ApplyAsync(newRoot, plan);
        }

        await _database.UpdateLibraryItemAsync(item.Id, kind, seriesId, title, _torrentPath!,
            _torrent!.InfoHash, season, outputDirectory);
        await _database.ReplaceStreamsAsync(item.Id, streams);
        ResetEditor();
        await RefreshLibraryAsync();
        SetStatus($"Изменения сохранены: создано {plan.Create.Count}, обновлено {plan.Update.Count}, " +
                  $"удалено {plan.Delete.Count}.");
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

    private async Task SaveSettingsAsync(AppSettings settings, bool validate)
    {
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

    private async Task SwitchDatabaseAsync(string path)
    {
        var database = new LibraryDatabase(path);
        await database.InitializeAsync();
        _database = database;
        await _bootstrap.SaveAsync(database.DatabasePath);
        _settings = await database.GetSettingsAsync();
        await RefreshLibraryAsync();
    }

    private async Task RefreshLibraryAsync()
    {
        var items = await _database.GetLibraryAsync();
        _allLibrary.Clear();
        _allLibrary.AddRange(items.Select(item => new LibraryRow(item)));
        ApplyLibraryFilter();
    }

    private void LibrarySearch_Changed(object? sender, TextChangedEventArgs e) => ApplyLibraryFilter();

    private void ShowAll_Click(object? sender, RoutedEventArgs e)
    {
        _libraryFilter = null;
        ApplyLibraryFilter();
    }

    private void ShowMovies_Click(object? sender, RoutedEventArgs e)
    {
        _libraryFilter = MediaKind.Movie;
        ApplyLibraryFilter();
    }

    private void ShowSeries_Click(object? sender, RoutedEventArgs e)
    {
        _libraryFilter = MediaKind.Series;
        ApplyLibraryFilter();
    }

    private void ApplyLibraryFilter()
    {
        if (LibrarySearchBox is null) return;
        var query = LibrarySearchBox.Text?.Trim() ?? "";
        var filtered = _allLibrary.Where(row =>
            (_libraryFilter is null || row.Item.Kind == _libraryFilter) &&
            (query.Length == 0 || row.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
        _library.Clear();
        foreach (var row in filtered)
            _library.Add(row);

        LibraryCountText.Text = _library.Count == _allLibrary.Count
            ? $"{_allLibrary.Count} источников"
            : $"{_library.Count} из {_allLibrary.Count} источников";
        UpdateFilterButtons();
    }

    private void UpdateFilterButtons()
    {
        var activeBackground = Avalonia.Media.Brush.Parse("#7B2018");
        var activeForeground = Avalonia.Media.Brushes.White;
        var normalBackground = Avalonia.Media.Brush.Parse("#E5E7EA");
        var normalForeground = Avalonia.Media.Brush.Parse("#24282E");
        foreach (var (button, active) in new[]
                 {
                     (ShowAllButton, _libraryFilter is null),
                     (ShowMoviesButton, _libraryFilter == MediaKind.Movie),
                     (ShowSeriesButton, _libraryFilter == MediaKind.Series)
                 })
        {
            button.Background = active ? activeBackground : normalBackground;
            button.Foreground = active ? activeForeground : normalForeground;
        }
    }

    private void Numbering_Changed(object? sender, EventArgs e)
    {
        if (_loadingEditor) return;
        if (_editingItem is null)
        {
            BuildEpisodePreview();
            return;
        }

        MarkEditorDirty();
        var episode = FirstEpisodeBox.Value;
        foreach (var row in _episodes.Where(x => x.Selected))
        {
            row.Season = SeasonBox.Value;
            row.Episode = episode++;
        }
    }
    private void MediaKind_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loadingEditor) return;
        var series = SeriesKindRadio?.IsChecked == true;
        ApplyMediaKindVisibility(series);
        BuildEpisodePreview();
        MarkEditorDirty();
    }

    private void ApplyMediaKindVisibility(bool series)
    {
        SeasonControls.IsVisible = series;
        EpisodeControls.IsVisible = series;
        SeasonColumnHeader.IsVisible = series;
        EpisodeColumnHeader.IsVisible = series;
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
        MarkEditorDirty();
    }

    private async void SettingsToggle_Click(object? sender, RoutedEventArgs e)
    {
        var result = await new SettingsWindow(_settings, _database.DatabasePath)
            .ShowDialog<SettingsWindowResult?>(this);
        if (result is null) return;
        try
        {
            if (!string.Equals(result.DatabasePath, _database.DatabasePath,
                    StringComparison.OrdinalIgnoreCase))
                await SwitchDatabaseAsync(result.DatabasePath);
            var previousSettings = _settings;
            await SaveSettingsAsync(result.Settings, validate: true);
            if (result.SyncNow)
            {
                var (created, updated) = await SyncLibraryAsync(previousSettings);
                SetStatus($"Настройки сохранены. Медиатека синхронизирована: " +
                          $"создано {created}, обновлено {updated}.");
            }
            else
            {
                SetStatus("Настройки сохранены. Синхронизацию медиатеки нужно запустить вручную.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Не удалось сохранить настройки: {exception.Message}", true);
        }
    }

    private async void LibraryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LibraryList.SelectedItem is not LibraryRow row) return;
        try
        {
            await LoadLibraryItemAsync(row.Item);
        }
        catch (Exception exception)
        {
            SetStatus($"Не удалось открыть источник для редактирования: {exception.Message}", true);
        }
    }

    private async Task LoadLibraryItemAsync(LibraryItem item)
    {
        if (!File.Exists(item.Source))
            throw new FileNotFoundException("Исходный torrent-файл не найден.", item.Source);

        _loadingEditor = true;
        try
        {
            _editingItem = item;
            _torrentPath = item.Source;
            _torrent = _torrentParser.Parse(item.Source);
            var streams = await _database.GetStreamsAsync(item.Id);
            TitleBox.ItemsSource = null;
            TitleBox.SelectedItem = null;
            TitleBox.Text = item.Title;
            SeriesKindRadio.IsChecked = item.Kind == MediaKind.Series;
            MovieKindRadio.IsChecked = item.Kind == MediaKind.Movie;
            SeasonBox.Value = item.SeasonNumber ?? 1;
            FirstEpisodeBox.Value = 1;
            ApplyMediaKindVisibility(item.Kind == MediaKind.Series);

            var storedByIndex = streams.ToDictionary(x => x.TorrentIndex);
            var candidates = Recognition.DetectEpisodes(_torrent, item.SeasonNumber ?? 1, 1);
            var visibleCandidates = item.Kind == MediaKind.Series
                ? candidates
                : candidates.Where(x => streams.Any(stream => stream.TorrentIndex == x.Source.Index));
            _episodes.Clear();
            foreach (var candidate in visibleCandidates)
            {
                var selected = storedByIndex.TryGetValue(candidate.Source.Index, out var stored);
                var (season, episode) = selected && item.Kind == MediaKind.Series
                    ? ParseNumbering(stored!.RelativePath, candidate.SeasonNumber, candidate.EpisodeNumber)
                    : (candidate.SeasonNumber, candidate.EpisodeNumber);
                AddEpisodeRow(new EpisodeRow(candidate.Source, season, episode,
                    () => CurrentTitle(), item.Kind == MediaKind.Series, selected));
            }
            if (item.Kind == MediaKind.Movie)
            {
                var mainIndex = streams.FirstOrDefault()?.TorrentIndex;
                foreach (var row in _episodes)
                    row.Selected = row.Index == mainIndex;
            }

            TorrentSummaryText.Text =
                $"{Path.GetFileName(item.Source)} | {_torrent.Files.Count} файлов | " +
                $"{_torrent.Files.Count(x => x.IsVideo())} видео | {_torrent.InfoHash}";
            EditorTitleText.Text = "Редактирование источника";
            ImportButton.Content = "Сохранить изменения";
            EpisodeList.IsVisible = _episodes.Count > 0;
            _editorSnapshot = CaptureEditorSnapshot();
            _editorDirty = false;
            UpdateImportButtonVisibility();
            SetStatus("Источник загружен. Измените данные и сохраните.");
        }
        finally
        {
            _loadingEditor = false;
        }
    }

    private static (int Season, int Episode) ParseNumbering(string path, int fallbackSeason,
        int fallbackEpisode)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(path),
            @"\bs(?<season>\d{1,3})e(?<episode>\d{1,4})\b", RegexOptions.IgnoreCase);
        return match.Success
            ? (int.Parse(match.Groups["season"].Value), int.Parse(match.Groups["episode"].Value))
            : (fallbackSeason, fallbackEpisode);
    }

    private void ResetEditor()
    {
        _editingItem = null;
        _torrent = null;
        _torrentPath = null;
        _seriesChoices = [];
        _episodes.Clear();
        TitleBox.ItemsSource = null;
        TitleBox.SelectedItem = null;
        TitleBox.Text = "";
        EditorTitleText.Text = "Добавление источника";
        TorrentSummaryText.Text = "Выберите torrent-файл, чтобы увидеть его содержимое";
        ImportButton.Content = "Добавить и синхронизировать";
        _editorDirty = false;
        _editorSnapshot = null;
        UpdateImportButtonVisibility();
        EpisodeList.IsVisible = false;
        LibraryList.SelectedItem = null;
    }

    private void AddEpisodeRow(EpisodeRow row)
    {
        row.PropertyChanged += (_, _) => MarkEditorDirty();
        _episodes.Add(row);
    }

    private void MarkEditorDirty()
    {
        if (_loadingEditor || _editingItem is null) return;
        _editorDirty = _editorSnapshot != CaptureEditorSnapshot();
        UpdateImportButtonVisibility();
    }

    private EditorSnapshot CaptureEditorSnapshot() =>
        new(CurrentTitle(),
            MovieKindRadio.IsChecked == true ? MediaKind.Movie : MediaKind.Series,
            string.Join("|", _episodes.Select(row =>
                $"{row.Index}:{row.Selected}:{row.Season}:{row.Episode}")));

    private void UpdateImportButtonVisibility() =>
        ImportButton.IsVisible = _torrent is not null && (_editingItem is null || _editorDirty);

    private async void SyncAll_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync(_settings, validate: true);
            var (created, updated) = await SyncLibraryAsync(_settings);
            SetStatus($"Медиатека синхронизирована: создано {created}, обновлено {updated}.");
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private async Task<(int Created, int Updated)> SyncLibraryAsync(AppSettings previousSettings)
    {
        var created = 0;
        var updated = 0;
        foreach (var item in await _database.GetLibraryAsync())
        {
            var streams = await _database.GetStreamsAsync(item.Id);
            var oldRoot = item.Kind == MediaKind.Series
                ? previousSettings.SeriesPath
                : previousSettings.MoviesPath;
            var root = item.Kind == MediaKind.Series ? _settings.SeriesPath : _settings.MoviesPath;
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException(item.Kind == MediaKind.Series
                    ? "Не указан каталог сериалов."
                    : "Не указан каталог фильмов.");
            if (string.IsNullOrWhiteSpace(oldRoot))
                oldRoot = root;
            var revised = streams.Select(x => x with
            {
                Content = RebaseServer(x.Content, _settings.ServerUrl)
            }).ToArray();
            SyncPlan plan;
            if (!string.Equals(Path.GetFullPath(oldRoot), Path.GetFullPath(root),
                    StringComparison.OrdinalIgnoreCase))
            {
                var removal = await _synchronizer.PlanAsync(oldRoot, [], streams);
                await _synchronizer.ApplyAsync(oldRoot, removal);
                plan = await _synchronizer.PlanAsync(root, revised, []);
            }
            else
            {
                plan = await _synchronizer.PlanAsync(root, revised, streams);
            }
            await _synchronizer.ApplyAsync(root, plan);
            await _database.ReplaceStreamsAsync(item.Id, revised);
            created += plan.Create.Count;
            updated += plan.Update.Count;
        }
        return (created, updated);
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

    private sealed record EditorSnapshot(string Title, MediaKind Kind, string Rows);

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
        public EpisodeRow(TorrentFile source, int season, int episode, Func<string> title, bool isSeries,
            bool selected = true) =>
            (Source, _season, _episode, _title, IsSeries, _selected) =
            (source, season, episode, title, isSeries, selected);
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
