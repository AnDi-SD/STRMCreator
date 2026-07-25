using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using STRMCreator.App.Localization;
using STRMCreator.Core;
using STRMCreator.Infrastructure;

namespace STRMCreator.App;

public partial class MainWindow : Window
{
    private LibraryDatabase _database;
    private readonly BootstrapConfigStore _bootstrap = new();
    private readonly StreamSynchronizer _synchronizer = new();
    private readonly TorrentParser _torrentParser = new();
    private readonly ObservableCollection<EpisodeRow> _episodes = [];
    private readonly ObservableCollection<LibraryRow> _library = [];
    private readonly List<LibraryRow> _allLibrary = [];
    private TorrentMetadata? _torrent;
    private string? _torrentPath;
    private AppSettings _settings = AppSettings.Default;
    private LibraryItem? _editingItem;
    private LibraryRow? _editingGroup;
    private bool _loadingEditor;
    private bool _editorDirty;
    private EditorSnapshot? _editorSnapshot;
    private MediaKind? _libraryFilter;
    private bool _suppressLibrarySelection;

    public MainWindow()
    {
        InitializeComponent();
        _database = new LibraryDatabase(_bootstrap.DefaultDatabasePath);
        EpisodeList.ItemsSource = _episodes;
        LibraryList.ItemsSource = _library;
        EnglishLanguageContent.IsVisible = LocalizationManager.Language == "en";
        RussianLanguageContent.IsVisible = LocalizationManager.Language == "ru";
        Opened += async (_, _) => await InitializeAsync();
    }

    private async void Language_Click(object? sender, RoutedEventArgs e)
    {
        var language = LocalizationManager.Language == "en" ? "ru" : "en";
        await _bootstrap.SaveLanguageAsync(language);
        LocalizationManager.SetLanguage(language);

        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var replacement = new MainWindow
        {
            Width = Bounds.Width,
            Height = Bounds.Height,
            Position = Position,
            WindowState = WindowState
        };
        desktop.MainWindow = replacement;
        replacement.Show();
        Close();
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
            ResetEditor();
            await CheckMissingStreamsAsync();
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, true);
        }
    }

    private async void AddSource_Click(object? sender, RoutedEventArgs e)
    {
        var result = await new AddSourceWindow(_database).ShowDialog<AddSourceResult?>(this);
        if (result is null) return;
        try
        {
            await ImportNewSourceAsync(result);
        }
        catch (Exception exception)
        {
            SetStatus($"Could not add the source: {exception.Message}", true);
        }
    }

    private async Task ImportNewSourceAsync(AddSourceResult result)
    {
        await SaveSettingsAsync(_settings, validate: true);
        await _database.StoreTorrentAsync(result.Torrent.InfoHash, result.TorrentData, result.MagnetUri);
        var root = result.Kind == MediaKind.Series ? _settings.SeriesPath : _settings.MoviesPath;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException(result.Kind == MediaKind.Series
                ? "The TV shows folder is not configured."
                : "The movies folder is not configured.");
        var outputDirectory = OutputPath.SanitizeSegment(result.Title);
        long? seriesId = result.Kind == MediaKind.Series
            ? await _database.GetOrCreateSeriesAsync(result.Title, result.Torrent.Name)
            : null;
        IEnumerable<IEnumerable<AddSourceFile>> groups = result.Kind == MediaKind.Series
            ? result.Files.Where(file => file.Selected).GroupBy(file => file.Season)
            : [result.Files.Where(file => file.Selected)];

        var created = 0;
        var updated = 0;
        foreach (var group in groups)
        {
            var files = group.ToArray();
            var season = result.Kind == MediaKind.Series ? files[0].Season : (int?)null;
            var itemId = await _database.UpsertLibraryItemAsync(result.Kind, seriesId, result.Title,
                $"embedded:{result.Torrent.InfoHash}", result.Torrent.InfoHash, season, outputDirectory);
            var previous = await _database.GetStreamsAsync(itemId);
            var streams = files.Select(file =>
            {
                var outputName = result.Kind == MediaKind.Series
                    ? $"{OutputPath.SanitizeSegment(result.Title)} s{file.Season:00}e{file.Episode:00}.strm"
                    : $"{OutputPath.SanitizeSegment(result.Title)}.strm";
                return new ManagedStream(0, itemId, file.Source.Index, file.Source.Path,
                    $"{outputDirectory}/{outputName}",
                    StreamUrlBuilder.Build(_settings.ServerUrl, result.Torrent.InfoHash, file.Source));
            }).ToArray();
            var plan = await _synchronizer.PlanAsync(root, streams, previous);
            await _synchronizer.ApplyAsync(root, plan);
            await _database.ReplaceStreamsAsync(itemId, streams);
            created += plan.Create.Count;
            updated += plan.Update.Count;
        }

        await RefreshLibraryAsync();
        ResetEditor();
        SetStatus($"Source added: {created} created, {updated} updated.");
    }

    private async void ImportAndSync_Click(object? sender, RoutedEventArgs e)
    {
        if (_torrent is null || _torrentPath is null || _editingItem is null)
        {
            SetStatus("Select a library item first.", true);
            return;
        }

        try
        {
            await SaveSettingsAsync(_settings, validate: true);
            var kind = MovieKindRadio.IsChecked == true ? MediaKind.Movie : MediaKind.Series;
            var title = CurrentTitle();
            if (string.IsNullOrWhiteSpace(title))
                throw new InvalidOperationException("Enter a title.");
            var outputDirectory = OutputPath.SanitizeSegment(title);
            long? seriesId = null;
            if (kind == MediaKind.Series)
                seriesId = await _database.GetOrCreateSeriesAsync(title, _torrent.Name);
            await SaveEditedItemAsync(kind, seriesId, title, outputDirectory);
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
        if (_editingGroup is { Items.Count: > 1 } && kind != _editingGroup.Kind)
            throw new InvalidOperationException(
                "The category cannot be changed for an item with multiple seasons or sources.");
        var selected = _episodes.Where(x => x.Selected).ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException("Select at least one video file.");
        var seasons = selected.Select(x => x.Season).Distinct().ToArray();
        if (_editingGroup is not null &&
            !string.Equals(_editingGroup.Title, title, StringComparison.CurrentCulture))
            await RenameOtherGroupItemsAsync(_editingGroup, item.Id, title, seriesId, outputDirectory);
        var seasonChanged = kind == MediaKind.Series &&
                            selected.Any(row => row.Season != item.SeasonNumber);
        if (seasonChanged)
        {
            await MoveEpisodesBetweenSeasonsAsync(item, title, seriesId, outputDirectory, selected);
            return;
        }

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
        if (!string.Equals(item.OutputDirectory, outputDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(oldRoot), Path.GetFullPath(newRoot),
                StringComparison.OrdinalIgnoreCase))
            _synchronizer.DeleteDirectoryIfEmpty(oldRoot, item.OutputDirectory);

        await _database.UpdateLibraryItemAsync(item.Id, kind, seriesId, title, _torrentPath!,
            _torrent!.InfoHash, season, outputDirectory);
        await _database.ReplaceStreamsAsync(item.Id, streams);
        await RefreshAndSelectAsync(kind, title, season);
        SetStatus($"Changes saved: {plan.Create.Count} created, {plan.Update.Count} updated, " +
                  $"{plan.Delete.Count} deleted.");
    }

    private async Task MoveEpisodesBetweenSeasonsAsync(LibraryItem sourceItem, string title,
        long? seriesId, string outputDirectory, IReadOnlyList<EpisodeRow> rows)
    {
        var root = _settings.SeriesPath;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("The TV shows folder is not configured.");

        var previous = (await _database.GetStreamsAsync(sourceItem.Id)).ToList();
        var desiredByItem = new Dictionary<long, List<ManagedStream>>();
        foreach (var seasonGroup in rows.GroupBy(row => row.Season))
        {
            var targetId = await _database.UpsertLibraryItemAsync(MediaKind.Series, seriesId, title,
                sourceItem.Source, sourceItem.InfoHash, seasonGroup.Key, outputDirectory);
            var targetStreams = targetId == sourceItem.Id
                ? []
                : (await _database.GetStreamsAsync(targetId)).ToList();
            if (targetId != sourceItem.Id)
                previous.AddRange(targetStreams);

            var moved = seasonGroup.Select(row =>
                new ManagedStream(0, targetId, row.Source.Index, row.Source.Path,
                    $"{outputDirectory}/{OutputPath.SanitizeSegment(title)} " +
                    $"s{row.Season:00}e{row.Episode:00}.strm",
                    StreamUrlBuilder.Build(_settings.ServerUrl, sourceItem.InfoHash, row.Source))).ToArray();
            var movedPaths = moved.Select(stream => stream.RelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (targetStreams.Any(stream => movedPaths.Contains(stream.RelativePath)))
                throw new InvalidOperationException(
                    $"Season {seasonGroup.Key} already contains an episode with this number.");
            targetStreams.AddRange(moved);
            desiredByItem[targetId] = targetStreams;
        }

        var desired = desiredByItem.Values.SelectMany(streams => streams).ToArray();
        var plan = await _synchronizer.PlanAsync(root, desired, previous);
        await _synchronizer.ApplyAsync(root, plan);

        var affectedIds = previous.Select(stream => stream.LibraryItemId)
            .Append(sourceItem.Id).Concat(desiredByItem.Keys).Distinct().ToArray();
        foreach (var itemId in affectedIds)
        {
            if (desiredByItem.TryGetValue(itemId, out var streams))
                await _database.ReplaceStreamsAsync(itemId, streams);
            else
                await _database.DeleteLibraryItemAsync(itemId);
        }

        await RefreshAndSelectAsync(MediaKind.Series, title, rows[0].Season);
        SetStatus($"Episodes moved between seasons: {plan.Create.Count} created, " +
                  $"{plan.Update.Count} updated, {plan.Delete.Count} deleted.");
    }

    private async Task RenameOtherGroupItemsAsync(LibraryRow group, long currentItemId,
        string title, long? seriesId, string outputDirectory)
    {
        var root = group.Kind == MediaKind.Series ? _settings.SeriesPath : _settings.MoviesPath;
        foreach (var item in group.Items.Where(candidate => candidate.Id != currentItemId))
        {
            var previous = await _database.GetStreamsAsync(item.Id);
            var revised = previous.Select(stream =>
            {
                var match = Regex.Match(Path.GetFileNameWithoutExtension(stream.RelativePath),
                    @"\bs(?<season>\d{1,3})e(?<episode>\d{1,4})\b", RegexOptions.IgnoreCase);
                var name = group.Kind == MediaKind.Series && match.Success
                    ? $"{OutputPath.SanitizeSegment(title)} s{int.Parse(match.Groups["season"].Value):00}" +
                      $"e{int.Parse(match.Groups["episode"].Value):00}.strm"
                    : $"{OutputPath.SanitizeSegment(title)}.strm";
                return stream with { RelativePath = $"{outputDirectory}/{name}" };
            }).ToArray();
            var plan = await _synchronizer.PlanAsync(root, revised, previous);
            await _synchronizer.ApplyAsync(root, plan);
            if (!string.Equals(item.OutputDirectory, outputDirectory,
                    StringComparison.OrdinalIgnoreCase))
                _synchronizer.DeleteDirectoryIfEmpty(root, item.OutputDirectory);
            await _database.UpdateLibraryItemAsync(item.Id, item.Kind, seriesId, title,
                item.Source, item.InfoHash, item.SeasonNumber, outputDirectory);
            await _database.ReplaceStreamsAsync(item.Id, revised);
        }
    }

    private IReadOnlyList<ManagedStream> BuildStreams(long itemId, MediaKind kind, string title,
        string directory, IReadOnlyList<EpisodeRow>? rows = null)
    {
        if (_torrent is null) return [];
        if (kind == MediaKind.Movie)
        {
            var video = _torrent.Files.Where(x => x.IsVideo()).OrderByDescending(x => x.Length).FirstOrDefault()
                        ?? throw new InvalidOperationException("The torrent contains no supported video files.");
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
            throw new InvalidOperationException("Enter a valid absolute TorrServer address.");
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
        _allLibrary.AddRange(items
            .GroupBy(item => (item.Kind, Recognition.NormalizeTitle(item.Title)))
            .Select(group => new LibraryRow(group.OrderBy(item => item.SeasonNumber).ToArray())));
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
            (_libraryFilter is null || row.Kind == _libraryFilter) &&
            (query.Length == 0 || row.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
        _library.Clear();
        foreach (var row in filtered)
            _library.Add(row);

        LibraryCountText.Text = _library.Count == _allLibrary.Count
            ? $"{_allLibrary.Count} sources"
            : $"{_library.Count} of {_allLibrary.Count} sources";
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

    private void MediaKind_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loadingEditor) return;
        var series = SeriesKindRadio?.IsChecked == true;
        ApplyMediaKindVisibility(series);
        MarkEditorDirty();
    }

    private void ApplyMediaKindVisibility(bool series)
    {
        SeasonControls.IsVisible = series;
        PlaySeasonButton.IsVisible = series;
        EpisodeControls.IsVisible = true;
        SeasonColumnHeader.IsVisible = series;
        EpisodeColumnHeader.IsVisible = series;
    }

    private void TitleBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
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
                SetStatus($"Settings saved. Library synchronized: " +
                          $"{created} created, {updated} updated.");
            }
            else
            {
                SetStatus("Settings saved. Library synchronization must be started manually.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Could not save settings: {exception.Message}", true);
        }
    }

    private async void LibraryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressLibrarySelection) return;
        DeleteSelectedButton.IsEnabled = LibraryList.SelectedItem is LibraryRow;
        if (LibraryList.SelectedItem is not LibraryRow row) return;
        try
        {
            await LoadLibraryGroupAsync(row);
        }
        catch (Exception exception)
        {
            SetStatus($"Could not open the source for editing: {exception.Message}", true);
        }
    }

    private async Task LoadLibraryGroupAsync(LibraryRow row, int? preferredSeason = null)
    {
        _editingGroup = row;
        _loadingEditor = true;
        try
        {
            var seasons = row.Items.Where(item => item.SeasonNumber.HasValue)
                .Select(item => item.SeasonNumber!.Value).Distinct().Order()
                .Select(value => new SeasonChoice(value)).ToArray();
            SeasonSelector.ItemsSource = seasons;
            SeasonSelector.SelectedItem = seasons.FirstOrDefault(choice =>
                choice.Number == preferredSeason) ?? seasons.FirstOrDefault();
        }
        finally
        {
            _loadingEditor = false;
        }
        var item = preferredSeason.HasValue
            ? row.Items.FirstOrDefault(candidate => candidate.SeasonNumber == preferredSeason) ?? row.Item
            : row.Item;
        await LoadLibraryItemAsync(item);
    }

    private async Task RefreshAndSelectAsync(MediaKind kind, string title, int? season)
    {
        await RefreshLibraryAsync();
        var normalized = Recognition.NormalizeTitle(title);
        var row = _library.FirstOrDefault(candidate =>
            candidate.Kind == kind && Recognition.NormalizeTitle(candidate.Title) == normalized);
        if (row is null)
        {
            ResetEditor();
            return;
        }

        _suppressLibrarySelection = true;
        try
        {
            LibraryList.SelectedItem = row;
            DeleteSelectedButton.IsEnabled = true;
        }
        finally
        {
            _suppressLibrarySelection = false;
        }
        await LoadLibraryGroupAsync(row, season);
    }

    private async void SeasonSelector_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingEditor || _editingGroup is null ||
            SeasonSelector.SelectedItem is not SeasonChoice choice)
            return;
        var item = _editingGroup.Items.FirstOrDefault(candidate => candidate.SeasonNumber == choice.Number);
        if (item is not null)
            await LoadLibraryItemAsync(item);
    }

    private async void ShowSources_Click(object? sender, RoutedEventArgs e)
    {
        if (_editingGroup is null) return;
        var request = await new LinkedSourcesWindow(_editingGroup.Title, _editingGroup.Items, _database)
            .ShowDialog<ReassignTorrentRequest?>(this);
        if (request is null) return;
        try
        {
            await ReassignTorrentAsync(_editingGroup, request);
        }
        catch (Exception exception)
        {
            SetStatus($"Could not relink the torrent: {exception.Message}", true);
        }
    }

    private async void ShowAllSources_Click(object? sender, RoutedEventArgs e)
    {
        var request = await new LinkedSourcesWindow(_database)
            .ShowDialog<ReassignTorrentRequest?>(this);
        if (request is null) return;
        try
        {
            var ids = request.ItemIds.ToHashSet();
            var items = (await _database.GetLibraryAsync())
                .Where(item => ids.Contains(item.Id)).OrderBy(item => item.SeasonNumber).ToArray();
            if (items.Length == 0)
                throw new InvalidOperationException("The selected link no longer exists.");
            await ReassignTorrentAsync(new LibraryRow(items), request);
        }
        catch (Exception exception)
        {
            SetStatus($"Could not relink the torrent: {exception.Message}", true);
        }
    }

    private async Task ReassignTorrentAsync(LibraryRow group, ReassignTorrentRequest request)
    {
        var requestedIds = request.ItemIds.ToHashSet();
        var sourceItems = group.Items.Where(item => requestedIds.Contains(item.Id) &&
            string.Equals(item.InfoHash, request.InfoHash, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sourceItems.Length == 0)
            throw new InvalidOperationException("The selected torrent is no longer linked to a library item.");
        var previous = new List<ManagedStream>();
        foreach (var item in sourceItems)
            previous.AddRange(await _database.GetStreamsAsync(item.Id));
        if (request.TargetKind == MediaKind.Movie && previous.Count != 1)
            throw new InvalidOperationException(
                "Only a torrent with one selected video file can be linked to a movie.");
        if (request.TargetKind == MediaKind.Movie && sourceItems.Length != 1)
            throw new InvalidOperationException(
                "A source containing multiple seasons cannot be converted to a movie.");

        var oldRoot = group.Kind == MediaKind.Series ? _settings.SeriesPath : _settings.MoviesPath;
        var newRoot = request.TargetKind == MediaKind.Series ? _settings.SeriesPath : _settings.MoviesPath;
        if (string.IsNullOrWhiteSpace(oldRoot) || string.IsNullOrWhiteSpace(newRoot))
            throw new InvalidOperationException("Library folders are not configured.");

        var outputDirectory = OutputPath.SanitizeSegment(request.TargetTitle);
        long? seriesId = request.TargetKind == MediaKind.Series
            ? await _database.GetOrCreateSeriesAsync(request.TargetTitle)
            : null;
        var revisedByItem = new Dictionary<long, List<ManagedStream>>();
        foreach (var item in sourceItems)
        {
            var streams = await _database.GetStreamsAsync(item.Id);
            var revised = streams.Select((stream, index) =>
            {
                var parsed = ParseNumbering(stream.RelativePath, item.SeasonNumber ?? 1, index + 1);
                var name = request.TargetKind == MediaKind.Series
                    ? $"{outputDirectory} s{parsed.Season:00}e{parsed.Episode:00}.strm"
                    : $"{outputDirectory}.strm";
                return stream with { RelativePath = $"{outputDirectory}/{name}" };
            }).ToList();
            revisedByItem[item.Id] = revised;
        }

        var desired = revisedByItem.Values.SelectMany(streams => streams).ToArray();
        var targetItems = (await _database.GetLibraryAsync()).Where(item =>
            item.Kind == request.TargetKind &&
            Recognition.NormalizeTitle(item.Title) == Recognition.NormalizeTitle(request.TargetTitle) &&
            !sourceItems.Any(source => source.Id == item.Id)).ToArray();
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in targetItems)
            foreach (var stream in await _database.GetStreamsAsync(item.Id))
                occupied.Add(stream.RelativePath.Replace('\\', '/'));
        if (desired.Any(stream => occupied.Contains(stream.RelativePath.Replace('\\', '/'))))
            throw new InvalidOperationException(
                "The target item already contains episodes with these numbers.");

        if (string.Equals(Path.GetFullPath(oldRoot), Path.GetFullPath(newRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            var plan = await _synchronizer.PlanAsync(newRoot, desired, previous);
            await _synchronizer.ApplyAsync(newRoot, plan);
        }
        else
        {
            await _synchronizer.ApplyAsync(oldRoot,
                await _synchronizer.PlanAsync(oldRoot, [], previous));
            await _synchronizer.ApplyAsync(newRoot,
                await _synchronizer.PlanAsync(newRoot, desired, []));
        }
        foreach (var directory in sourceItems.Select(item => item.OutputDirectory)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            if (!string.Equals(directory, outputDirectory, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(oldRoot), Path.GetFullPath(newRoot),
                    StringComparison.OrdinalIgnoreCase))
                _synchronizer.DeleteDirectoryIfEmpty(oldRoot, directory);

        foreach (var item in sourceItems)
        {
            var season = request.TargetKind == MediaKind.Series ? item.SeasonNumber ?? 1 : (int?)null;
            await _database.UpdateLibraryItemAsync(item.Id, request.TargetKind, seriesId,
                request.TargetTitle, item.Source, item.InfoHash, season, outputDirectory);
            await _database.ReplaceStreamsAsync(item.Id, revisedByItem[item.Id]);
        }

        ResetEditor();
        await RefreshLibraryAsync();
        SetStatus($"Torrent relinked to \"{request.TargetTitle}\".");
    }

    private async void ShowMediaFiles_Click(object? sender, RoutedEventArgs e)
    {
        if (_editingGroup is null) return;
        var mappings = await new MediaFilesWindow(_editingGroup.Title, _editingGroup.Items, _database)
            .ShowDialog<IReadOnlyList<MediaFileMapping>?>(this);
        if (mappings is null) return;
        try
        {
            await ApplyMediaMappingsAsync(_editingGroup, mappings);
        }
        catch (Exception exception)
        {
            SetStatus($"Could not apply the assignment: {exception.Message}", true);
        }
    }

    private async Task ApplyMediaMappingsAsync(LibraryRow group,
        IReadOnlyList<MediaFileMapping> mappings)
    {
        var root = group.Kind == MediaKind.Series ? _settings.SeriesPath : _settings.MoviesPath;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("The library folder is not configured.");
        var previous = new List<ManagedStream>();
        foreach (var item in group.Items)
            previous.AddRange(await _database.GetStreamsAsync(item.Id));

        long? seriesId = group.Kind == MediaKind.Series
            ? await _database.GetOrCreateSeriesAsync(group.Title)
            : null;
        var outputDirectory = OutputPath.SanitizeSegment(group.Title);
        var desiredByItem = new Dictionary<long, List<ManagedStream>>();
        foreach (var sourceGroup in mappings.Where(mapping => mapping.Selected)
                     .GroupBy(mapping => (mapping.SourceItem.InfoHash,
                         Season: group.Kind == MediaKind.Series ? mapping.Season : (int?)null)))
        {
            var source = sourceGroup.First().SourceItem;
            var itemId = await _database.UpsertLibraryItemAsync(group.Kind, seriesId, group.Title,
                source.Source, source.InfoHash, sourceGroup.Key.Season, outputDirectory);
            var streams = sourceGroup.Select(mapping =>
            {
                var name = group.Kind == MediaKind.Series
                    ? $"{outputDirectory} s{mapping.Season:00}e{mapping.Episode:00}.strm"
                    : $"{outputDirectory}.strm";
                return new ManagedStream(0, itemId, mapping.File.Index, mapping.File.Path,
                    $"{outputDirectory}/{name}",
                    StreamUrlBuilder.Build(_settings.ServerUrl, source.InfoHash, mapping.File));
            }).ToList();
            desiredByItem[itemId] = streams;
        }

        var desired = desiredByItem.Values.SelectMany(streams => streams).ToArray();
        var plan = await _synchronizer.PlanAsync(root, desired, previous);
        await _synchronizer.ApplyAsync(root, plan);

        var affectedIds = group.Items.Select(item => item.Id)
            .Concat(desiredByItem.Keys).Distinct().ToArray();
        foreach (var itemId in affectedIds)
        {
            if (desiredByItem.TryGetValue(itemId, out var streams))
                await _database.ReplaceStreamsAsync(itemId, streams);
            else
                await _database.DeleteLibraryItemAsync(itemId);
        }

        var preferredSeason = group.Kind == MediaKind.Series
            ? mappings.Where(mapping => mapping.Selected).Select(mapping => (int?)mapping.Season)
                .Order().FirstOrDefault()
            : null;
        await RefreshAndSelectAsync(group.Kind, group.Title, preferredSeason);
        SetStatus($"Assignment saved: {plan.Create.Count} created, " +
                  $"{plan.Update.Count} updated, {plan.Delete.Count} deleted.");
    }

    private async void DeleteSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is LibraryRow row)
            await ConfirmAndDeleteAsync(row);
    }

    private async void DeleteLibraryItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: LibraryRow row })
        {
            LibraryList.SelectedItem = row;
            await ConfirmAndDeleteAsync(row);
        }
    }

    private async Task ConfirmAndDeleteAsync(LibraryRow row)
    {
        var detail = row.Kind == MediaKind.Series
            ? $"{row.Title} ({row.Items.Count} seasons/sources)"
            : row.Title;
        if (!await new DeleteLibraryItemDialog(detail).ShowDialog<bool>(this))
            return;

        try
        {
            var root = row.Kind == MediaKind.Series ? _settings.SeriesPath : _settings.MoviesPath;
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("The library folder is not configured.");
            foreach (var item in row.Items)
            {
                var streams = await _database.GetStreamsAsync(item.Id);
                var removal = await _synchronizer.PlanAsync(root, [], streams);
                await _synchronizer.ApplyAsync(root, removal);
                await _database.DeleteLibraryItemAsync(item.Id);
            }
            if (_editingItem is not null && row.Items.Any(item => item.Id == _editingItem.Id))
                ResetEditor();
            await RefreshLibraryAsync();
            SetStatus($"Deleted from library: {detail}.");
        }
        catch (Exception exception)
        {
            SetStatus($"Could not delete the source: {exception.Message}", true);
        }
    }

    private async void DeleteEpisode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EpisodeRow row })
            await ConfirmAndDeleteEpisodeAsync(row);
    }

    private async void DeleteEpisodeMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: EpisodeRow row })
            await ConfirmAndDeleteEpisodeAsync(row);
    }

    private async Task ConfirmAndDeleteEpisodeAsync(EpisodeRow row)
    {
        if (_editingItem is null || _editingItem.Kind != MediaKind.Series) return;
        if (!await new DeleteEpisodeDialog(_editingItem.Title, row.Season, row.Episode)
                .ShowDialog<bool>(this))
            return;

        try
        {
            var title = _editingItem.Title;
            var preferredSeason = _editingItem.SeasonNumber;
            var previous = await _database.GetStreamsAsync(_editingItem.Id);
            var desired = previous.Where(stream => stream.TorrentIndex != row.Index).ToArray();
            if (desired.Length == previous.Count)
                throw new InvalidOperationException("The episode link no longer exists.");
            var root = _settings.SeriesPath;
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("The TV shows folder is not configured.");
            var plan = await _synchronizer.PlanAsync(root, desired, previous);
            await _synchronizer.ApplyAsync(root, plan);
            if (desired.Length == 0)
                await _database.DeleteLibraryItemAsync(_editingItem.Id);
            else
                await _database.ReplaceStreamsAsync(_editingItem.Id, desired);

            await RefreshAndSelectAsync(MediaKind.Series, title,
                desired.Length > 0 ? preferredSeason : null);
            SetStatus($"Episode deleted: season {row.Season:00}, episode {row.Episode:00}.");
        }
        catch (Exception exception)
        {
            SetStatus($"Could not delete the episode: {exception.Message}", true);
        }
    }

    private async void PlayEpisode_Click(object? sender, RoutedEventArgs e)
    {
        if (_editingItem is null || sender is not Button { DataContext: EpisodeRow row }) return;
        try
        {
            var stream = (await _database.GetStreamsAsync(_editingItem.Id))
                .FirstOrDefault(candidate => candidate.TorrentIndex == row.Index)
                ?? throw new InvalidOperationException("Media file link not found.");
            var display = row.IsSeries
                ? $"{_editingItem.Title} S{row.Season:00}E{row.Episode:00}"
                : _editingItem.Title;
            await OpenPlaylistAsync(display, [(display, stream.Content)]);
        }
        catch (Exception exception)
        {
            SetStatus($"Could not open the media player: {exception.Message}", true);
        }
    }

    private async void PlaySeason_Click(object? sender, RoutedEventArgs e)
    {
        if (_editingGroup is null || SeasonSelector.SelectedItem is not SeasonChoice choice) return;
        try
        {
            var entries = new List<(int Episode, string Display, string Content)>();
            foreach (var item in _editingGroup.Items.Where(candidate =>
                         candidate.SeasonNumber == choice.Number))
            {
                foreach (var stream in await _database.GetStreamsAsync(item.Id))
                {
                    var numbering = ParseNumbering(stream.RelativePath, choice.Number, 0);
                    entries.Add((numbering.Episode,
                        $"{_editingGroup.Title} S{choice.Number:00}E{numbering.Episode:00}",
                        stream.Content));
                }
            }
            if (entries.Count == 0)
                throw new InvalidOperationException("The selected season contains no linked episodes.");
            var ordered = entries.OrderBy(entry => entry.Episode)
                .Select(entry => (entry.Display, entry.Content)).ToArray();
            await OpenPlaylistAsync($"{_editingGroup.Title} - season {choice.Number}", ordered);
        }
        catch (Exception exception)
        {
            SetStatus($"Could not open the season: {exception.Message}", true);
        }
    }

    private async Task OpenPlaylistAsync(string name,
        IReadOnlyList<(string Display, string Content)> entries)
    {
        var directory = Path.Combine(Path.GetTempPath(), "STRMCreator", "playlists");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{OutputPath.SanitizeSegment(name)}.m3u");
        var playlist = new StringBuilder("#EXTM3U\n");
        foreach (var entry in entries)
        {
            var display = entry.Display.Replace('\r', ' ').Replace('\n', ' ');
            playlist.Append("#EXTINF:-1,").Append(display).Append('\n')
                .Append(RebaseServer(entry.Content, _settings.ServerUrl)).Append('\n');
        }
        await File.WriteAllTextAsync(path, playlist.ToString(), new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        SetStatus(entries.Count == 1
            ? "Playlist opened in the default media player."
            : $"Playlist opened with {entries.Count} episodes.");
    }

    private async Task LoadLibraryItemAsync(LibraryItem item)
    {
        _loadingEditor = true;
        try
        {
            _editingItem = item;
            _torrentPath = item.Source;
            _torrent = _torrentParser.Parse(await _database.GetTorrentDataAsync(item.InfoHash));
            var streams = await _database.GetStreamsAsync(item.Id);
            TitleBox.Text = item.Title;
            SeriesKindRadio.IsChecked = item.Kind == MediaKind.Series;
            MovieKindRadio.IsChecked = item.Kind == MediaKind.Movie;
            ApplyMediaKindVisibility(item.Kind == MediaKind.Series);

            var storedByIndex = streams.ToDictionary(x => x.TorrentIndex);
            var candidates = Recognition.DetectEpisodes(_torrent, item.SeasonNumber ?? 1, 1);
            var visibleCandidates = candidates.Where(candidate =>
                storedByIndex.ContainsKey(candidate.Source.Index));
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
                $"{_torrent.Name} | {_torrent.Files.Count} files | " +
                $"{_torrent.Files.Count(x => x.IsVideo())} videos | {_torrent.InfoHash}";
            EditorTitleText.Text = "Edit source";
            EditorFieldsPanel.IsVisible = true;
            EditorContentPanel.IsVisible = true;
            ImportButton.Content = LocalizationManager.Get("SaveChanges");
            EpisodeList.IsVisible = _episodes.Count > 0;
            _editorSnapshot = CaptureEditorSnapshot();
            _editorDirty = false;
            UpdateImportButtonVisibility();
            SetStatus("Source loaded. Edit the data and save your changes.");
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
        _editingGroup = null;
        _torrent = null;
        _torrentPath = null;
        _episodes.Clear();
        TitleBox.Text = "";
        EditorTitleText.Text = LocalizationManager.Get("SelectLibraryItem");
        TorrentSummaryText.Text = LocalizationManager.Get("EditorHint");
        ImportButton.Content = LocalizationManager.Get("AddAndSync");
        _editorDirty = false;
        _editorSnapshot = null;
        UpdateImportButtonVisibility();
        EpisodeList.IsVisible = false;
        EditorFieldsPanel.IsVisible = false;
        EditorContentPanel.IsVisible = false;
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
            SetStatus($"Library synchronized: {created} created, {updated} updated.");
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
                    ? "The TV shows folder is not configured."
                    : "The movies folder is not configured.");
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

    private async Task CheckMissingStreamsAsync()
    {
        var missing = new List<(string Root, IReadOnlyList<ManagedStream> Streams)>();
        foreach (var item in await _database.GetLibraryAsync())
        {
            var root = item.Kind == MediaKind.Series ? _settings.SeriesPath : _settings.MoviesPath;
            if (string.IsNullOrWhiteSpace(root)) continue;
            var streams = await _database.GetStreamsAsync(item.Id);
            var plan = await _synchronizer.PlanAsync(root, streams, streams);
            if (plan.Create.Count > 0)
                missing.Add((root, plan.Create));
        }

        var count = missing.Sum(group => group.Streams.Count);
        if (count == 0 || !await new RestoreStreamsDialog(count).ShowDialog<bool>(this))
            return;

        foreach (var group in missing)
            await _synchronizer.ApplyAsync(group.Root,
                new SyncPlan(group.Streams, [], [], []));
        SetStatus($"STRM files restored: {count}.");
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

    private sealed record EditorSnapshot(string Title, MediaKind Kind, string Rows);

    private sealed record SeasonChoice(int Number)
    {
        public override string ToString() => $"Season {Number}";
    }

    private sealed record LibraryRow(IReadOnlyList<LibraryItem> Items)
    {
        public LibraryItem Item => Items[0];
        public string Title => Item.Title;
        public MediaKind Kind => Item.Kind;
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
