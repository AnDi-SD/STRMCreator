using System.Text.Json;

namespace STRMshelf.Infrastructure;

public sealed record BootstrapConfig(string DatabasePath, string Language = "en");

public sealed class BootstrapConfigStore
{
    private readonly string _configPath;
    private readonly string _legacyConfigPath;
    public string DefaultDatabasePath { get; }

    public BootstrapConfigStore()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(localData, "STRMshelf");
        var legacyDirectory = Path.Combine(localData, "STRMCreator");
        _configPath = Path.Combine(directory, "config.json");
        _legacyConfigPath = Path.Combine(legacyDirectory, "config.json");

        var databasePath = Path.Combine(directory, "library.db");
        var legacyDatabasePath = Path.Combine(legacyDirectory, "library.db");
        DefaultDatabasePath = !File.Exists(databasePath) && File.Exists(legacyDatabasePath)
            ? legacyDatabasePath
            : databasePath;
    }

    public async Task<BootstrapConfig> LoadAsync()
    {
        var configPath = ExistingConfigPath();
        if (configPath is null)
            return new BootstrapConfig(DefaultDatabasePath);
        try
        {
            await using var stream = File.OpenRead(configPath);
            return await JsonSerializer.DeserializeAsync<BootstrapConfig>(stream)
                   ?? new BootstrapConfig(DefaultDatabasePath);
        }
        catch (JsonException)
        {
            return new BootstrapConfig(DefaultDatabasePath);
        }
    }

    public BootstrapConfig Load()
    {
        var configPath = ExistingConfigPath();
        if (configPath is null)
            return new BootstrapConfig(DefaultDatabasePath);
        try
        {
            return JsonSerializer.Deserialize<BootstrapConfig>(File.ReadAllText(configPath))
                   ?? new BootstrapConfig(DefaultDatabasePath);
        }
        catch (JsonException)
        {
            return new BootstrapConfig(DefaultDatabasePath);
        }
    }

    public async Task SaveAsync(string databasePath)
    {
        var current = await LoadAsync();
        await SaveAsync(new BootstrapConfig(Path.GetFullPath(databasePath), current.Language));
    }

    public async Task SaveLanguageAsync(string language)
    {
        var current = await LoadAsync();
        await SaveAsync(current with { Language = language });
    }

    private async Task SaveAsync(BootstrapConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        var temporary = _configPath + ".tmp";
        await File.WriteAllTextAsync(temporary,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _configPath, true);
    }

    private string? ExistingConfigPath()
    {
        if (File.Exists(_configPath))
            return _configPath;

        // Keep existing installations usable after the STRMshelf rename.
        return File.Exists(_legacyConfigPath) ? _legacyConfigPath : null;
    }
}
