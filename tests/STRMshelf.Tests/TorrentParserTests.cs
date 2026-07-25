using System.Security.Cryptography;
using System.Text;
using STRMshelf.Core;

namespace STRMshelf.Tests;

public sealed class TorrentParserTests
{
    [Fact]
    public void Parse_MultiFileTorrent_PreservesOneBasedIndexesAndComputesInfoHash()
    {
        const string info =
            "d5:filesl" +
            "d6:lengthi100e4:pathl11:episode.mkvee" +
            "d6:lengthi10e4:pathl11:episode.srtee" +
            "e4:name4:Showe";
        var bytes = Encoding.ASCII.GetBytes($"d4:info{info}e");
        var expectedHash = Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes(info)))
            .ToLowerInvariant();

        var result = new TorrentParser().Parse(bytes);

        Assert.Equal("Show", result.Name);
        Assert.Equal(expectedHash, result.InfoHash);
        Assert.Collection(result.Files,
            video =>
            {
                Assert.Equal(1, video.Index);
                Assert.Equal("episode.mkv", video.Path);
            },
            subtitle =>
            {
                Assert.Equal(2, subtitle.Index);
                Assert.Equal("episode.srt", subtitle.Path);
            });
    }
}
