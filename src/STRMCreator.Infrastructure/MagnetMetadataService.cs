using MonoTorrent;
using MonoTorrent.Client;
using STRMCreator.Core;

namespace STRMCreator.Infrastructure;

public sealed class MagnetMetadataService
{
    public async Task<string> DownloadAsync(string magnetUri, string destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (!MagnetLink.TryParse(magnetUri, out var magnet))
            throw new FormatException("Некорректная magnet-ссылка.");

        Directory.CreateDirectory(destinationDirectory);
        var cacheDirectory = Path.Combine(destinationDirectory, ".metadata-cache");
        Directory.CreateDirectory(cacheDirectory);
        var settings = new EngineSettingsBuilder
        {
            AllowPortForwarding = false,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            CacheDirectory = cacheDirectory
        }.ToSettings();

        using var engine = new ClientEngine(settings);
        var metadata = await engine.DownloadMetadataAsync(magnet, cancellationToken);
        var parsed = new TorrentParser().Parse(metadata);
        var name = OutputPath.SanitizeSegment(parsed.Name);
        var path = Path.Combine(destinationDirectory, $"{name} [{parsed.InfoHash}].torrent");
        await File.WriteAllBytesAsync(path, metadata, cancellationToken);
        return path;
    }
}
