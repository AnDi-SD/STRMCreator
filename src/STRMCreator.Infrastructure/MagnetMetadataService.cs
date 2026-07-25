using MonoTorrent;
using MonoTorrent.Client;
using STRMCreator.Core;

namespace STRMCreator.Infrastructure;

public sealed class MagnetMetadataService
{
    public async Task<byte[]> DownloadDataAsync(string magnetUri, string cacheDirectory,
        CancellationToken cancellationToken)
    {
        if (!MagnetLink.TryParse(magnetUri, out var magnet))
            throw new FormatException("Invalid magnet link.");

        Directory.CreateDirectory(cacheDirectory);
        var settings = new EngineSettingsBuilder
        {
            AllowPortForwarding = false,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            CacheDirectory = cacheDirectory
        }.ToSettings();

        using var engine = new ClientEngine(settings);
        return (await engine.DownloadMetadataAsync(magnet, cancellationToken)).ToArray();
    }

    public async Task<string> DownloadAsync(string magnetUri, string destinationDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var cacheDirectory = Path.Combine(destinationDirectory, ".metadata-cache");
        var metadata = await DownloadDataAsync(magnetUri, cacheDirectory, cancellationToken);
        var parsed = new TorrentParser().Parse(metadata);
        var name = OutputPath.SanitizeSegment(parsed.Name);
        var path = Path.Combine(destinationDirectory, $"{name} [{parsed.InfoHash}].torrent");
        await File.WriteAllBytesAsync(path, metadata, cancellationToken);
        return path;
    }
}
