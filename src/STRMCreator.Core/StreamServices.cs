namespace STRMCreator.Core;

public static class StreamUrlBuilder
{
    public static string Build(string serverUrl, string infoHash, TorrentFile file)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server))
            throw new ArgumentException("TorrServer URL must be absolute.", nameof(serverUrl));
        return $"{server.ToString().TrimEnd('/')}/stream/{Uri.EscapeDataString(file.Name)}" +
               $"?link={infoHash}&index={file.Index}&play";
    }
}

public static class OutputPath
{
    public static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Where(character => !invalid.Contains(character)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "Untitled" : result;
    }
}
