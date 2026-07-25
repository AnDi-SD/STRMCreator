using MonoTorrent;
using MonoTorrent.Client;

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
}
