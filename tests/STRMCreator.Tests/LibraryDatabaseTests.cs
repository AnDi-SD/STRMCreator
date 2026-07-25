using STRMCreator.Core;
using STRMCreator.Infrastructure;

namespace STRMCreator.Tests;

public sealed class LibraryDatabaseTests
{
    [Fact]
    public async Task Backup_CreatesConsistentPortableDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"strmcreator-tests-{Guid.NewGuid():N}");
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
        var directory = Path.Combine(Path.GetTempPath(), $"strmcreator-tests-{Guid.NewGuid():N}");
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
    public async Task MagnetService_RejectsInvalidLinkBeforeNetworkAccess()
    {
        var service = new MagnetMetadataService();
        await Assert.ThrowsAsync<FormatException>(() =>
            service.DownloadAsync("not-a-magnet", Path.GetTempPath(), CancellationToken.None));
    }
}
