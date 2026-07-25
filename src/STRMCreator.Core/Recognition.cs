using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace STRMCreator.Core;

public static partial class Recognition
{
    private static readonly Regex[] EpisodePatterns =
        [SeasonEpisodeRegex(), XPatternRegex(), EpisodeWordRegex(), ShortEpisodeRegex(),
            RussianEpisodeRegex(), TransliterationEpisodeRegex()];

    public static string SuggestTitle(string torrentName)
    {
        var value = BracketNoiseRegex().Replace(torrentName, " ");
        value = CountRegex().Replace(value, " ");
        value = SeasonRegex().Replace(value, " ");
        value = YearAndAfterRegex().Replace(value, " ");
        value = ReleaseNoiseRegex().Replace(value, " ");
        value = SeparatorRegex().Replace(value, " ").Trim(' ', '.', '-', '_');
        return string.IsNullOrWhiteSpace(value) ? torrentName.Trim() : value;
    }

    public static MediaKind DetectMediaKind(TorrentMetadata torrent) =>
        torrent.Files.Count(file => file.IsVideo()) <= 1 ? MediaKind.Movie : MediaKind.Series;

    public static IReadOnlyList<EpisodeCandidate> DetectEpisodes(
        TorrentMetadata torrent, int defaultSeason = 1, int firstEpisode = 1)
    {
        var result = new List<EpisodeCandidate>();
        var fallbackBySeason = new Dictionary<int, int>();
        foreach (var file in torrent.Files.Where(x => x.IsVideo()))
        {
            var season = defaultSeason;
            int? episode = null;
            var seasonMatch = SeasonOnlyRegex().Match(file.Path);
            if (seasonMatch.Success)
                season = int.Parse(seasonMatch.Groups["season"].Value, CultureInfo.InvariantCulture);
            foreach (var regex in EpisodePatterns)
            {
                var match = regex.Match(file.Path);
                if (!match.Success) continue;
                if (match.Groups["season"].Success)
                    season = int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture);
                episode = int.Parse(match.Groups["episode"].Value, CultureInfo.InvariantCulture);
                break;
            }
            var fallback = fallbackBySeason.GetValueOrDefault(season, firstEpisode);
            result.Add(new EpisodeCandidate(file, season, episode ?? fallback));
            fallbackBySeason[season] = Math.Max(fallback + 1, (episode ?? fallback) + 1);
        }
        return result;
    }

    public static string NormalizeTitle(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark &&
                char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    public static double Similarity(string left, string right)
    {
        left = NormalizeTitle(left);
        right = NormalizeTitle(right);
        if (left == right) return 1;
        if (left.Length == 0 || right.Length == 0) return 0;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return 1d - (double)previous[right.Length] / Math.Max(left.Length, right.Length);
    }

    [GeneratedRegex(@"(?i)s(?<season>\d{1,2})[\s._-]*e(?<episode>\d{1,3})")]
    private static partial Regex SeasonEpisodeRegex();
    [GeneratedRegex(@"(?i)(?<season>\d{1,2})x(?<episode>\d{1,3})")]
    private static partial Regex XPatternRegex();
    [GeneratedRegex(@"(?i)(?:episode|ep)[\s._-]*(?<episode>\d{1,3})")]
    private static partial Regex EpisodeWordRegex();
    [GeneratedRegex(@"(?i)(?:^|[/\\\s._-])e(?<episode>\d{1,3})(?:[/\\\s._-]|$)")]
    private static partial Regex ShortEpisodeRegex();
    [GeneratedRegex(@"(?i)(?<episode>\d{1,3})[\s._-]*(?:серия|серии|сер)")]
    private static partial Regex RussianEpisodeRegex();
    [GeneratedRegex(@"(?i)\(?(?<episode>\d{1,3})[\s._-]*serija")]
    private static partial Regex TransliterationEpisodeRegex();
    [GeneratedRegex(@"\[[^\]]+\]|\{[^}]+\}")]
    private static partial Regex BracketNoiseRegex();
    [GeneratedRegex(@"(?i)\b\d+\s*(?:из|of)\s*\d+\b")]
    private static partial Regex CountRegex();
    [GeneratedRegex(@"(?i)\b(?:season|сезон|s)\s*\d{1,3}(?:\s*[-–]\s*\d{1,3})?\b")]
    private static partial Regex SeasonRegex();
    [GeneratedRegex(@"(?i)(?:^|[/\\\s._-])(?:season|сезон|s)[\s._-]*(?<season>\d{1,3})(?:[/\\\s._-]|$)")]
    private static partial Regex SeasonOnlyRegex();
    [GeneratedRegex(@"\((?:19|20)\d{2}\).*|(?:19|20)\d{2}.*")]
    private static partial Regex YearAndAfterRegex();
    [GeneratedRegex(@"(?i)\b(?:web-?dl|bluray|bdrip|dvdrip|webrip|hdtv|xvid|x26[45]|hevc|avc|1080p|720p|2160p)\b.*")]
    private static partial Regex ReleaseNoiseRegex();
    [GeneratedRegex(@"[\s._]+")]
    private static partial Regex SeparatorRegex();
}
