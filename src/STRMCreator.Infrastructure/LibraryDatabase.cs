using Microsoft.Data.Sqlite;
using STRMCreator.Core;

namespace STRMCreator.Infrastructure;

public sealed class LibraryDatabase(string databasePath)
{
    static LibraryDatabase()
    {
        if (OperatingSystem.IsWindows())
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        else
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
        SQLitePCL.raw.FreezeProvider();
    }

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS settings (
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS series (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              name TEXT NOT NULL COLLATE NOCASE UNIQUE,
              normalized_name TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS series_aliases (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              series_id INTEGER NOT NULL REFERENCES series(id) ON DELETE CASCADE,
              name TEXT NOT NULL,
              normalized_name TEXT NOT NULL UNIQUE
            );
            CREATE TABLE IF NOT EXISTS library_items (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              kind INTEGER NOT NULL,
              series_id INTEGER REFERENCES series(id),
              title TEXT NOT NULL,
              source TEXT NOT NULL,
              info_hash TEXT NOT NULL,
              season_number INTEGER,
              output_directory TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              UNIQUE(kind, info_hash, season_number)
            );
            CREATE TABLE IF NOT EXISTS managed_streams (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              library_item_id INTEGER NOT NULL REFERENCES library_items(id) ON DELETE CASCADE,
              torrent_index INTEGER NOT NULL,
              torrent_path TEXT NOT NULL,
              relative_path TEXT NOT NULL,
              content TEXT NOT NULL,
              UNIQUE(library_item_id, relative_path)
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<AppSettings> GetSettingsAsync()
    {
        var values = new Dictionary<string, string>();
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values[reader.GetString(0)] = reader.GetString(1);
        var defaults = AppSettings.Default;
        return new AppSettings(
            values.GetValueOrDefault("server_url", defaults.ServerUrl),
            values.GetValueOrDefault("movies_path", defaults.MoviesPath),
            values.GetValueOrDefault("series_path", defaults.SeriesPath));
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var pair in new Dictionary<string, string>
                 {
                     ["server_url"] = settings.ServerUrl.TrimEnd('/'),
                     ["movies_path"] = settings.MoviesPath,
                     ["series_path"] = settings.SeriesPath
                 })
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                "INSERT INTO settings(key,value) VALUES($key,$value) " +
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$key", pair.Key);
            command.Parameters.AddWithValue("$value", pair.Value);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<SeriesMatch>> FindSeriesAsync(string query)
    {
        var candidates = new Dictionary<long, (string Name, HashSet<int> Seasons, double Score)>();
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.id, s.name, s.normalized_name, a.normalized_name,
                   GROUP_CONCAT(DISTINCT li.season_number)
            FROM series s
            LEFT JOIN series_aliases a ON a.series_id=s.id
            LEFT JOIN library_items li ON li.series_id=s.id
            GROUP BY s.id, s.name, s.normalized_name, a.normalized_name
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt64(0);
            var name = reader.GetString(1);
            var score = Math.Max(
                Recognition.Similarity(query, reader.GetString(2)),
                reader.IsDBNull(3) ? 0 : Recognition.Similarity(query, reader.GetString(3)));
            var seasons = reader.IsDBNull(4)
                ? []
                : reader.GetString(4).Split(',').Select(int.Parse).ToHashSet();
            if (!candidates.TryGetValue(id, out var current) || score > current.Score)
                candidates[id] = (name, seasons, score);
            else
                current.Seasons.UnionWith(seasons);
        }
        return candidates.Select(x => new SeriesMatch(x.Key, x.Value.Name, x.Value.Score,
                x.Value.Seasons.Order().ToArray()))
            .Where(x => x.Score >= 0.35)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name)
            .ToArray();
    }

    public async Task<long> GetOrCreateSeriesAsync(string name, string? alias = null)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO series(name,normalized_name) VALUES($name,$normalized)
            ON CONFLICT(name) DO UPDATE SET normalized_name=excluded.normalized_name
            RETURNING id
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$normalized", Recognition.NormalizeTitle(name));
        var id = (long)(await command.ExecuteScalarAsync())!;
        if (!string.IsNullOrWhiteSpace(alias) && Recognition.NormalizeTitle(alias) != Recognition.NormalizeTitle(name))
        {
            command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO series_aliases(series_id,name,normalized_name) VALUES($id,$name,$normalized)
                ON CONFLICT(normalized_name) DO UPDATE SET series_id=excluded.series_id,name=excluded.name
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", alias);
            command.Parameters.AddWithValue("$normalized", Recognition.NormalizeTitle(alias));
            await command.ExecuteNonQueryAsync();
        }
        return id;
    }

    public async Task<long> UpsertLibraryItemAsync(MediaKind kind, long? seriesId, string title,
        string source, string infoHash, int? season, string outputDirectory)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO library_items(kind,series_id,title,source,info_hash,season_number,output_directory,updated_at)
            VALUES($kind,$series,$title,$source,$hash,$season,$output,$updated)
            ON CONFLICT(kind,info_hash,season_number) DO UPDATE SET
              series_id=excluded.series_id,title=excluded.title,source=excluded.source,
              output_directory=excluded.output_directory,updated_at=excluded.updated_at
            RETURNING id
            """;
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$series", (object?)seriesId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$hash", infoHash);
        command.Parameters.AddWithValue("$season", (object?)season ?? DBNull.Value);
        command.Parameters.AddWithValue("$output", outputDirectory);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task ReplaceStreamsAsync(long libraryItemId, IReadOnlyList<ManagedStream> streams)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var delete = connection.CreateCommand();
        delete.Transaction = (SqliteTransaction)transaction;
        delete.CommandText = "DELETE FROM managed_streams WHERE library_item_id=$id";
        delete.Parameters.AddWithValue("$id", libraryItemId);
        await delete.ExecuteNonQueryAsync();
        foreach (var stream in streams)
        {
            var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText =
                """
                INSERT INTO managed_streams(library_item_id,torrent_index,torrent_path,relative_path,content)
                VALUES($item,$index,$torrentPath,$relativePath,$content)
                """;
            insert.Parameters.AddWithValue("$item", libraryItemId);
            insert.Parameters.AddWithValue("$index", stream.TorrentIndex);
            insert.Parameters.AddWithValue("$torrentPath", stream.TorrentPath);
            insert.Parameters.AddWithValue("$relativePath", stream.RelativePath);
            insert.Parameters.AddWithValue("$content", stream.Content);
            await insert.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<LibraryItem>> GetLibraryAsync()
    {
        var result = new List<LibraryItem>();
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id,kind,title,source,info_hash,season_number,output_directory,updated_at " +
            "FROM library_items ORDER BY title,season_number";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new LibraryItem(reader.GetInt64(0), (MediaKind)reader.GetInt32(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7))));
        return result;
    }

    public async Task<IReadOnlyList<ManagedStream>> GetStreamsAsync(long libraryItemId)
    {
        var result = new List<ManagedStream>();
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id,library_item_id,torrent_index,torrent_path,relative_path,content " +
            "FROM managed_streams WHERE library_item_id=$id";
        command.Parameters.AddWithValue("$id", libraryItemId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new ManagedStream(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return result;
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
