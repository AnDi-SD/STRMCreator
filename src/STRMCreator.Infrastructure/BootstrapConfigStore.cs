using System.Text.Json;

namespace STRMCreator.Infrastructure;

public sealed record BootstrapConfig(string DatabasePath, string Language = "en");

public sealed class BootstrapConfigStore
{
    private readonly string _configPath;
    public string DefaultDatabasePath { get; }

    public BootstrapConfigStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STRMCreator");
        _configPath = Path.Combine(directory, "config.json");
        DefaultDatabasePath = Path.Combine(directory, "library.db");
    }

    public async Task<BootstrapConfig> LoadAsync()
    {
        if (!File.Exists(_configPath))
            return new BootstrapConfig(DefaultDatabasePath);
        try
        {
            await using var stream = File.OpenRead(_configPath);
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
        if (!File.Exists(_configPath))
            return new BootstrapConfig(DefaultDatabasePath);
        try
        {
            return JsonSerializer.Deserialize<BootstrapConfig>(File.ReadAllText(_configPath))
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
}
