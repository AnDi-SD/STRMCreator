using STRMCreator.Infrastructure;

namespace STRMCreator.Tests;

public sealed class StreamSynchronizerTests
{
    [Fact]
    public void DeleteDirectoryIfEmpty_RemovesOnlyEmptyManagedDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"strmcreator-tests-{Guid.NewGuid():N}");
        var empty = Path.Combine(root, "Old title");
        var occupied = Path.Combine(root, "Keep title");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "poster.jpg"), "keep");
        try
        {
            var synchronizer = new StreamSynchronizer();

            synchronizer.DeleteDirectoryIfEmpty(root, "Old title");
            synchronizer.DeleteDirectoryIfEmpty(root, "Keep title");

            Assert.False(Directory.Exists(empty));
            Assert.True(Directory.Exists(occupied));
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
