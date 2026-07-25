using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using STRMCreator.Core;
using STRMCreator.Infrastructure;

namespace STRMCreator.App;

public sealed record SettingsWindowResult(AppSettings Settings, string DatabasePath, bool SyncNow);

public partial class SettingsWindow : Window
{
    private string _databasePath;

    public SettingsWindow()
    {
        InitializeComponent();
        _databasePath = "";
    }

    public SettingsWindow(AppSettings settings, string databasePath) : this()
    {
        ServerUrlBox.Text = settings.ServerUrl;
        MoviesPathBox.Text = settings.MoviesPath;
        SeriesPathBox.Text = settings.SeriesPath;
        _databasePath = databasePath;
        DatabasePathBox.Text = databasePath;
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

    private async void OpenDatabase_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите существующую базу",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SQLite database") { Patterns = ["*.db"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            var database = new LibraryDatabase(path);
            await database.InitializeAsync();
            var settings = await database.GetSettingsAsync();
            _databasePath = database.DatabasePath;
            DatabasePathBox.Text = _databasePath;
            ServerUrlBox.Text = settings.ServerUrl;
            MoviesPathBox.Text = settings.MoviesPath;
            SeriesPathBox.Text = settings.SeriesPath;
            SetStatus("База открыта. Её настройки загружены.");
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private async void CreateDatabase_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickDatabasePathAsync("Создать новую базу");
        if (path is null) return;
        _databasePath = Path.GetFullPath(path);
        DatabasePathBox.Text = _databasePath;
        SetStatus("Новая база будет создана при сохранении настроек.");
    }

    private async void MoveDatabase_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickDatabasePathAsync("Перенести текущую базу");
        if (path is null) return;
        try
        {
            var database = new LibraryDatabase(_databasePath);
            await database.BackupAsync(path);
            _databasePath = Path.GetFullPath(path);
            DatabasePathBox.Text = _databasePath;
            SetStatus("База перенесена. Новый файл выбран как активный.");
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private async void BackupDatabase_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickDatabasePathAsync("Резервная копия базы");
        if (path is null) return;
        try
        {
            await new LibraryDatabase(_databasePath).BackupAsync(path);
            SetStatus($"Резервная копия создана: {path}");
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private async Task<string?> PickDatabasePathAsync(string title)
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

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var decision = await new SyncSettingsDialog().ShowDialog<bool?>(this);
        if (decision is null) return;
        var settings = new AppSettings(ServerUrlBox.Text?.Trim() ?? "",
            MoviesPathBox.Text?.Trim() ?? "", SeriesPathBox.Text?.Trim() ?? "");
        Close(new SettingsWindowResult(settings, _databasePath, decision.Value));
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? Avalonia.Media.Brushes.Red : Avalonia.Media.Brushes.DimGray;
    }
}
