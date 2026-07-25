using STRMshelf.Core;

namespace STRMshelf.Tests;

public sealed class RecognitionTests
{
    [Fact]
    public void DetectEpisodes_UsesSourceIndexesAndSerijaNumbers()
    {
        var torrent = new TorrentMetadata("Show", "hash",
        [
            new TorrentFile(1, "Show.(01.serija.iz.24).avi", 100),
            new TorrentFile(2, "Show.(01.serija.iz.24).srt", 10),
            new TorrentFile(3, "Show.(02.serija.iz.24).avi", 100)
        ]);

        var result = Recognition.DetectEpisodes(torrent, defaultSeason: 2);

        Assert.Equal([1, 3], result.Select(x => x.Source.Index));
        Assert.Equal([1, 2], result.Select(x => x.EpisodeNumber));
        Assert.All(result, x => Assert.Equal(2, x.SeasonNumber));
    }

    [Theory]
    [InlineData("Show.S03E07.1080p.mkv", 3, 7)]
    [InlineData("Show.2x11.mkv", 2, 11)]
    [InlineData("Show Episode 9.mkv", 1, 9)]
    public void DetectEpisodes_RecognizesCommonPatterns(string fileName, int season, int episode)
    {
        var torrent = new TorrentMetadata("Show", "hash", [new TorrentFile(1, fileName, 100)]);
        var result = Assert.Single(Recognition.DetectEpisodes(torrent));
        Assert.Equal(season, result.SeasonNumber);
        Assert.Equal(episode, result.EpisodeNumber);
    }

    [Fact]
    public void Similarity_IgnoresSpacingPunctuationAndCase() =>
        Assert.Equal(1, Recognition.Similarity("Full Metal Panic!", "fullmetalpanic"));

    [Fact]
    public void DetectMediaKind_UsesVideoFileCountAndIgnoresOtherFiles()
    {
        var movie = new TorrentMetadata("Movie", "movie",
        [
            new TorrentFile(1, "Movie.mkv", 1000),
            new TorrentFile(2, "Movie.srt", 10)
        ]);
        var series = new TorrentMetadata("Series", "series",
        [
            new TorrentFile(1, "S01E01.mkv", 1000),
            new TorrentFile(2, "S01E02.mkv", 1000)
        ]);

        Assert.Equal(MediaKind.Movie, Recognition.DetectMediaKind(movie));
        Assert.Equal(MediaKind.Series, Recognition.DetectMediaKind(series));
    }

    [Fact]
    public void DetectEpisodes_RecognizesSeasonFoldersInOneTorrent()
    {
        var torrent = new TorrentMetadata("Show Seasons 1-2", "hash",
        [
            new TorrentFile(1, "Season 1/Show E01.mkv", 100),
            new TorrentFile(2, "Season 2/Show E01.mkv", 100)
        ]);

        var result = Recognition.DetectEpisodes(torrent);

        Assert.Equal([1, 2], result.Select(x => x.SeasonNumber));
        Assert.Equal([1, 1], result.Select(x => x.EpisodeNumber));
        Assert.Equal([1, 2], result.Select(x => x.Source.Index));
    }

    [Fact]
    public void StreamUrl_UsesTorrentIndexAndEscapesFileName()
    {
        var file = new TorrentFile(47, "folder/Full Metal Panic! 24.mkv", 100);
        var url = StreamUrlBuilder.Build("http://192.168.1.1:55551/", "abc123", file);
        Assert.Equal(
            "http://192.168.1.1:55551/stream/Full%20Metal%20Panic%21%2024.mkv?link=abc123&index=47&play",
            url);
    }
}
