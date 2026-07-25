using STRMshelf.Core;
using STRMshelf.Infrastructure;

namespace STRMshelf.Tests;

public sealed class LibraryDatabaseTests
{
    [Fact]
    public async Task Backup_CreatesConsistentPortableDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"strmshelf-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = new LibraryDatabase(Path.Combine(directory, "source.db"));
            await source.InitializeAsync();
            var expected = new AppSettings("http://server:8090", "movies", "series");
            await source.SaveSettingsAsync(expected);

            var backupPath = Path.Combine(directory, "backup.db");
            await source.BackupAsync(backupPath);

            var backup = new LibraryDatabase(backupPath);
            await backup.InitializeAsync();
            Assert.Equal(expected, await backup.GetSettingsAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateLibraryItem_ChangesExistingRecordWithoutCreatingDuplicate()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"strmshelf-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var database = new LibraryDatabase(Path.Combine(directory, "library.db"));
            await database.InitializeAsync();
            var id = await database.UpsertLibraryItemAsync(MediaKind.Series, null, "Old title",
                "source.torrent", "HASH", 1, "Old title");

            await database.UpdateLibraryItemAsync(id, MediaKind.Movie, null, "New title",
                "source.torrent", "HASH", null, "New title");

            var item = Assert.Single(await database.GetLibraryAsync());
            Assert.Equal(id, item.Id);
            Assert.Equal(MediaKind.Movie, item.Kind);
            Assert.Equal("New title", item.Title);
            Assert.Null(item.SeasonNumber);
            Assert.Equal("New title", item.OutputDirectory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UpsertMovie_ReusesItemWhenSeasonIsNull()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"strmshelf-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var database = new LibraryDatabase(Path.Combine(directory, "library.db"));
            await database.InitializeAsync();

            var first = await database.UpsertLibraryItemAsync(MediaKind.Movie, null, "First title",
                "source.torrent", "HASH", null, "First title");
            var second = await database.UpsertLibraryItemAsync(MediaKind.Movie, null, "Updated title",
                "embedded:HASH", "HASH", null, "Updated title");

            Assert.Equal(first, second);
            var item = Assert.Single(await database.GetLibraryAsync());
            Assert.Equal("Updated title", item.Title);
            Assert.Equal("embedded:HASH", item.Source);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UpsertSeries_PreservesAndReusesSpecialsSeasonZero()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"strmshelf-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var database = new LibraryDatabase(Path.Combine(directory, "library.db"));
            await database.InitializeAsync();

            var first = await database.UpsertLibraryItemAsync(MediaKind.Series, null, "Show",
                "source.torrent", "HASH", SeasonNumbers.Specials, "Show");
            var second = await database.UpsertLibraryItemAsync(MediaKind.Series, null, "Show",
                "embedded:HASH", "HASH", SeasonNumbers.Specials, "Show");

            Assert.Equal(first, second);
            var item = Assert.Single(await database.GetLibraryAsync());
            Assert.Equal(SeasonNumbers.Specials, item.SeasonNumber);
            Assert.Equal("embedded:HASH", item.Source);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteLibraryItem_RemovesItemAndItsManagedStreams()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"strmshelf-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var database = new LibraryDatabase(Path.Combine(directory, "library.db"));
            await database.InitializeAsync();
            var id = await database.UpsertLibraryItemAsync(MediaKind.Movie, null, "Movie",
                "source.torrent", "HASH", null, "Movie");
            await database.ReplaceStreamsAsync(id,
            [
                new ManagedStream(0, id, 1, "Movie.mkv", "Movie/Movie.strm", "content")
            ]);

            await database.DeleteLibraryItemAsync(id);

            Assert.Empty(await database.GetLibraryAsync());
            Assert.Empty(await database.GetStreamsAsync(id));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TorrentPayload_IsStoredInsideDatabaseWithMagnetUri()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"strmshelf-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var database = new LibraryDatabase(Path.Combine(directory, "library.db"));
            await database.InitializeAsync();
            byte[] expected = [1, 2, 3, 4, 5];
            const string magnet = "magnet:?xt=urn:btih:ABC";

            await database.StoreTorrentAsync("ABC", expected, magnet);
            File.Delete(Path.Combine(directory, "unused.torrent"));

            Assert.Equal(expected, await database.GetTorrentDataAsync("ABC"));
            Assert.Equal(magnet, await database.GetTorrentMagnetAsync("ABC"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MagnetService_RejectsInvalidLinkBeforeNetworkAccess()
    {
        var service = new MagnetMetadataService();
        await Assert.ThrowsAsync<FormatException>(() =>
            service.DownloadDataAsync("not-a-magnet", Path.GetTempPath(), CancellationToken.None));
    }
}
