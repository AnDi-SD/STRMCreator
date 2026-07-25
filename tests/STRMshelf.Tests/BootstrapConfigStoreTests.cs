using STRMshelf.Infrastructure;

namespace STRMshelf.Tests;

public sealed class BootstrapConfigStoreTests
{
    [Fact]
    public void Load_UsesDefaultsWhenConfigIsInvalid()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"strmshelf-tests-{Guid.NewGuid():N}");
        var appDirectory = Path.Combine(directory, "STRMshelf");
        Directory.CreateDirectory(appDirectory);
        try
        {
            File.WriteAllText(Path.Combine(appDirectory, "config.json"), "{not-json");

            var store = new BootstrapConfigStore(directory);
            var config = store.Load();

            Assert.Equal(store.DefaultDatabasePath, config.DatabasePath);
            Assert.Equal("en", config.Language);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_UsesLegacyDatabaseAfterRename()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"strmshelf-tests-{Guid.NewGuid():N}");
        var legacyDirectory = Path.Combine(directory, "STRMCreator");
        Directory.CreateDirectory(legacyDirectory);
        var legacyDatabase = Path.Combine(legacyDirectory, "library.db");
        try
        {
            File.WriteAllBytes(legacyDatabase, []);

            var store = new BootstrapConfigStore(directory);
            var config = store.Load();

            Assert.Equal(legacyDatabase, store.DefaultDatabasePath);
            Assert.Equal(legacyDatabase, config.DatabasePath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
