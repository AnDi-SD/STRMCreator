using System.Text;
using STRMCreator.Core;

namespace STRMCreator.Infrastructure;

public sealed class StreamSynchronizer
{
    public async Task<SyncPlan> PlanAsync(string root, IReadOnlyList<ManagedStream> desired,
        IReadOnlyList<ManagedStream> previous)
    {
        var create = new List<ManagedStream>();
        var update = new List<ManagedStream>();
        var unchanged = new List<ManagedStream>();
        var desiredPaths = desired.Select(x => Normalize(x.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var stream in desired)
        {
            var path = Resolve(root, stream.RelativePath);
            if (!File.Exists(path))
                create.Add(stream);
            else if (!string.Equals(await File.ReadAllTextAsync(path), stream.Content, StringComparison.Ordinal))
                update.Add(stream);
            else
                unchanged.Add(stream);
        }
        var delete = previous.Where(x => !desiredPaths.Contains(Normalize(x.RelativePath))).ToArray();
        return new SyncPlan(create, update, delete, unchanged);
    }

    public async Task ApplyAsync(string root, SyncPlan plan)
    {
        foreach (var stream in plan.Create.Concat(plan.Update))
            await WriteAtomicAsync(Resolve(root, stream.RelativePath), stream.Content);

        foreach (var stream in plan.Delete)
        {
            var path = Resolve(root, stream.RelativePath);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    public async Task ValidateWritableAsync(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Output path is not configured.");
        Directory.CreateDirectory(root);
        var testPath = Path.Combine(root, $".strmcreator-write-test-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(testPath, "test");
        File.Delete(testPath);
    }

    public void DeleteDirectoryIfEmpty(string root, string relativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativeDirectory))
            return;
        var fullRoot = Path.GetFullPath(root);
        var directory = Resolve(root, relativeDirectory);
        if (string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar),
                fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return;
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    private static async Task WriteAtomicAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private static string Resolve(string root, string relative)
    {
        // Managed relative paths must never escape the configured library root.
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Managed path escapes the selected media root.");
        return fullPath;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
