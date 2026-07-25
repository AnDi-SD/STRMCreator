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
    public async Task MagnetService_RejectsInvalidLinkBeforeNetworkAccess()
    {
        var service = new MagnetMetadataService();
        await Assert.ThrowsAsync<FormatException>(() =>
            service.DownloadAsync("not-a-magnet", Path.GetTempPath(), CancellationToken.None));
    }
}
