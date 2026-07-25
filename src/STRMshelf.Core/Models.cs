namespace STRMshelf.Core;

public enum MediaKind { Movie, Series }

public static class SeasonNumbers
{
    // TMDB and compatible media libraries reserve season zero for specials.
    public const int Specials = 0;
}

public sealed record TorrentFile(int Index, string Path, long Length)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path);
}

public sealed record TorrentMetadata(string Name, string InfoHash, IReadOnlyList<TorrentFile> Files);
public sealed record SeriesMatch(long Id, string Name, double Score, IReadOnlyList<int> Seasons);

public sealed record EpisodeCandidate(TorrentFile Source, int SeasonNumber, int EpisodeNumber, bool Selected = true)
{
    public string OutputName(string seriesName) => $"{seriesName} s{SeasonNumber:00}e{EpisodeNumber:00}.strm";
}

public sealed record LibraryItem(long Id, MediaKind Kind, string Title, string Source, string InfoHash,
    int? SeasonNumber, string OutputDirectory, DateTimeOffset UpdatedAt);

public sealed record ManagedStream(long Id, long LibraryItemId, int TorrentIndex, string TorrentPath,
    string RelativePath, string Content);

public sealed record AppSettings(string ServerUrl, string MoviesPath, string SeriesPath)
{
    public static AppSettings Default => new("http://127.0.0.1:8090", "", "");
}

public sealed record SyncPlan(IReadOnlyList<ManagedStream> Create, IReadOnlyList<ManagedStream> Update,
    IReadOnlyList<ManagedStream> Delete, IReadOnlyList<ManagedStream> Unchanged);

public static class MediaExtensions
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg",
        ".mts", ".ts", ".vob", ".webm", ".wmv"
    };

    public static bool IsVideo(this TorrentFile file) => VideoExtensions.Contains(file.Extension);
}
