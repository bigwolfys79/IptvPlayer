using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using IptvPlayer.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;

public interface IPlaylistCacheService
{
    Task<PlaylistCache?> LoadAsync(int playlistId);
    Task SaveAsync(int playlistId, PlaylistCache cache);

    /// <summary>Удаляет кэш плейлиста — при удалении плейлиста из настроек.</summary>
    Task DeleteAsync(int playlistId);
}

/// <summary>
/// Хранит кэш разобранного плейлиста в SQLite (iptvplayer_cache.db).
/// Заменяет прежнее JSON-хранилище для производительности:
/// пакетная вставка, SQL-фильтры, нет десериализации всего файла при запуске.
/// </summary>
public class PlaylistDatabaseService : IPlaylistCacheService
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer");

    private static readonly string DbPath = Path.Combine(CacheDirectory, "iptvplayer_cache.db");
    private static readonly string LegacyCacheFilePath = Path.Combine(CacheDirectory, "playlist_cache.json");

    private readonly ILogger<PlaylistDatabaseService> _logger;

    public PlaylistDatabaseService(ILogger<PlaylistDatabaseService> logger)
    {
        _logger = logger;
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS playlists (
                    id INTEGER PRIMARY KEY,
                    format_version INTEGER NOT NULL DEFAULT 4,
                    saved_at_utc TEXT NOT NULL,
                    portal_key TEXT
                );
                CREATE TABLE IF NOT EXISTS channels (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
                    name TEXT NOT NULL,
                    stream_url TEXT,
                    logo_url TEXT,
                    ""group"" TEXT,
                    tvg_id TEXT,
                    catchup_days INTEGER DEFAULT 0,
                    portal_request TEXT,
                    description TEXT,
                    year INTEGER DEFAULT 0,
                    genre TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_channels_playlist ON channels(playlist_id);";
            cmd.ExecuteNonQuery();

            // Миграция: добавить portal_key если нет
            try
            {
                var altCmd = connection.CreateCommand();
                altCmd.CommandText = "ALTER TABLE playlists ADD COLUMN portal_key TEXT";
                altCmd.ExecuteNonQuery();
            }
            catch { /* колонка уже есть */ }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось инициализировать SQLite БД.");
        }
    }

    public async Task<PlaylistCache?> LoadAsync(int playlistId)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            await connection.OpenAsync();

            var metaCmd = connection.CreateCommand();
            metaCmd.CommandText =
                "SELECT format_version, saved_at_utc, portal_key FROM playlists WHERE id = $id";
            metaCmd.Parameters.AddWithValue("$id", playlistId);

            await using var reader = await metaCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                // Нет записи в SQLite — попробуем мигрировать из JSON.
                var migrated = await TryMigrateFromJsonAsync(playlistId, connection);
                if (migrated != null) return migrated;
                return null;
            }

            var formatVersion = reader.GetInt32(0);
            var savedAtUtc = DateTime.Parse(reader.GetString(1));
            var portalKeyHash = reader.IsDBNull(2) ? null : reader.GetString(2);
            await reader.CloseAsync();

            var cache = new PlaylistCache
            {
                FormatVersion = formatVersion,
                SavedAtUtc = savedAtUtc,
                PortalKeyHash = portalKeyHash,
                Channels = new()
            };

            var channelsCmd = connection.CreateCommand();
            channelsCmd.CommandText =
                "SELECT name, stream_url, logo_url, \"group\", tvg_id, catchup_days, portal_request, description, year, genre FROM channels WHERE playlist_id = $id";
            channelsCmd.Parameters.AddWithValue("$id", playlistId);

            await using var channelReader = await channelsCmd.ExecuteReaderAsync();
            while (await channelReader.ReadAsync())
            {
                cache.Channels.Add(new CachedChannel
                {
                    Name = channelReader.GetString(0),
                    StreamUrl = channelReader.IsDBNull(1) ? null : channelReader.GetString(1),
                    LogoUrl = channelReader.IsDBNull(2) ? null : channelReader.GetString(2),
                    Group = channelReader.IsDBNull(3) ? null : channelReader.GetString(3),
                    TvgId = channelReader.IsDBNull(4) ? null : channelReader.GetString(4),
                    CatchupDays = channelReader.GetInt32(5),
                    PortalRequest = channelReader.IsDBNull(6) ? null : channelReader.GetString(6),
                    Description = channelReader.IsDBNull(7) ? null : channelReader.GetString(7),
                    Year = channelReader.GetInt32(8),
                    Genre = channelReader.IsDBNull(9) ? null : channelReader.GetString(9)
                });
            }

            _logger.LogInformation(
                "Кэш плейлиста {PlaylistId} загружен из SQLite ({Count} каналов).",
                playlistId, cache.Channels.Count);
            return cache;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Кэш плейлиста {PlaylistId} не читается из SQLite — будет перекачан.", playlistId);
            return null;
        }
    }

    public async Task SaveAsync(int playlistId, PlaylistCache cache)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            await connection.OpenAsync();

            await using var transaction = await connection.BeginTransactionAsync();

            // Upsert playlist metadata.
            var metaCmd = connection.CreateCommand();
            metaCmd.CommandText =
                "INSERT INTO playlists (id, format_version, saved_at_utc, portal_key) VALUES ($id, $ver, $date, $key) " +
                "ON CONFLICT(id) DO UPDATE SET format_version = $ver, saved_at_utc = $date, portal_key = $key";
            metaCmd.Parameters.AddWithValue("$id", playlistId);
            metaCmd.Parameters.AddWithValue("$ver", cache.FormatVersion);
            metaCmd.Parameters.AddWithValue("$date", cache.SavedAtUtc.ToString("O"));
            metaCmd.Parameters.AddWithValue("$key", (object?)cache.PortalKeyHash ?? DBNull.Value);
            await metaCmd.ExecuteNonQueryAsync();

            // Delete old channels.
            var deleteCmd = connection.CreateCommand();
            deleteCmd.CommandText = "DELETE FROM channels WHERE playlist_id = $id";
            deleteCmd.Parameters.AddWithValue("$id", playlistId);
            await deleteCmd.ExecuteNonQueryAsync();

            // Batch insert new channels (500 per batch).
            const int batchSize = 500;
            for (var offset = 0; offset < cache.Channels.Count; offset += batchSize)
            {
                var batch = cache.Channels.GetRange(offset, Math.Min(batchSize, cache.Channels.Count - offset));
                var insertCmd = connection.CreateCommand();
                insertCmd.CommandText =
                    "INSERT INTO channels (playlist_id, name, stream_url, logo_url, \"group\", tvg_id, catchup_days, portal_request, description, year, genre) " +
                    "VALUES ($pid, $name, $url, $logo, $group, $tvg, $catchup, $portal, $desc, $year, $genre)";

                foreach (var ch in batch)
                {
                    insertCmd.Parameters.Clear();
                    insertCmd.Parameters.AddWithValue("$pid", playlistId);
                    insertCmd.Parameters.AddWithValue("$name", ch.Name);
                    insertCmd.Parameters.AddWithValue("$url", (object?)ch.StreamUrl ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$logo", (object?)ch.LogoUrl ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$group", (object?)ch.Group ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$tvg", (object?)ch.TvgId ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$catchup", ch.CatchupDays);
                    insertCmd.Parameters.AddWithValue("$portal", (object?)StripPortalKey(ch.PortalRequest) ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$desc", (object?)ch.Description ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$year", ch.Year);
                    insertCmd.Parameters.AddWithValue("$genre", (object?)ch.Genre ?? DBNull.Value);
                    await insertCmd.ExecuteNonQueryAsync();
                }
            }

            await transaction.CommitAsync();
            _logger.LogInformation(
                "Кэш плейлиста {PlaylistId} сохранён в SQLite ({Count} каналов).",
                playlistId, cache.Channels.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить кэш плейлиста {PlaylistId} в SQLite.", playlistId);
        }
    }

    public Task DeleteAsync(int playlistId)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            connection.Open();

            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM channels WHERE playlist_id = $id; DELETE FROM playlists WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", playlistId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить кэш плейлиста {PlaylistId} из SQLite.", playlistId);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Разовая миграция: читает старый JSON-файл, записывает в SQLite, удаляет JSON.
    /// </summary>
    private async Task<PlaylistCache?> TryMigrateFromJsonAsync(int playlistId, SqliteConnection connection)
    {
        var path = playlistId == 1 && !File.Exists(LegacyCacheFilePath)
            ? null
            : Path.Combine(CacheDirectory, $"playlist_cache_{playlistId}.json");

        // Попытка миграции из legacy-файла для плейлиста 1.
        if (playlistId == 1 && !File.Exists(path) && File.Exists(LegacyCacheFilePath))
        {
            path = LegacyCacheFilePath;
        }

        if (path == null || !File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var cache = JsonSerializer.Deserialize<PlaylistCache>(json);
            if (cache == null) return null;

            _logger.LogInformation(
                "Миграция кэша плейлиста {PlaylistId}: JSON → SQLite ({Count} каналов).",
                playlistId, cache.Channels.Count);

            // Сохраняем в SQLite через транзакцию.
            await using var transaction = await connection.BeginTransactionAsync();

            var metaCmd = connection.CreateCommand();
            metaCmd.CommandText =
                "INSERT INTO playlists (id, format_version, saved_at_utc) VALUES ($id, $ver, $date) " +
                "ON CONFLICT(id) DO UPDATE SET format_version = $ver, saved_at_utc = $date";
            metaCmd.Parameters.AddWithValue("$id", playlistId);
            metaCmd.Parameters.AddWithValue("$ver", cache.FormatVersion);
            metaCmd.Parameters.AddWithValue("$date", cache.SavedAtUtc.ToString("O"));
            await metaCmd.ExecuteNonQueryAsync();

            const int batchSize = 500;
            for (var offset = 0; offset < cache.Channels.Count; offset += batchSize)
            {
                var batch = cache.Channels.GetRange(offset, Math.Min(batchSize, cache.Channels.Count - offset));
                var insertCmd = connection.CreateCommand();
                insertCmd.CommandText =
                    "INSERT INTO channels (playlist_id, name, stream_url, logo_url, \"group\", tvg_id, catchup_days, portal_request, description, year, genre) " +
                    "VALUES ($pid, $name, $url, $logo, $group, $tvg, $catchup, $portal, $desc, $year, $genre)";

                foreach (var ch in batch)
                {
                    insertCmd.Parameters.Clear();
                    insertCmd.Parameters.AddWithValue("$pid", playlistId);
                    insertCmd.Parameters.AddWithValue("$name", ch.Name);
                    insertCmd.Parameters.AddWithValue("$url", (object?)ch.StreamUrl ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$logo", (object?)ch.LogoUrl ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$group", (object?)ch.Group ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$tvg", (object?)ch.TvgId ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$catchup", ch.CatchupDays);
                    insertCmd.Parameters.AddWithValue("$portal", (object?)StripPortalKey(ch.PortalRequest) ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$desc", (object?)ch.Description ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$year", ch.Year);
                    insertCmd.Parameters.AddWithValue("$genre", (object?)ch.Genre ?? DBNull.Value);
                    await insertCmd.ExecuteNonQueryAsync();
                }
            }

            await transaction.CommitAsync();

            // Удаляем JSON-файл после успешной миграции.
            File.Delete(path);
            _logger.LogInformation("JSON-кэш плейлиста {PlaylistId} удалён после миграции.", playlistId);

            return cache;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось мигрировать JSON-кэш плейлиста {PlaylistId}.", playlistId);
            return null;
        }
    }

    /// <summary>
    /// Убирает поле "key" из кэшируемого запроса портала: при отправке запроса
    /// VideoPortalService подставляет актуальный ключ из source.PortalKey, так
    /// что хранить его в БД не нужно (и небезопасно — БД пишется открытым
    /// текстом). При ошибке разбора возвращается исходная строка.
    /// </summary>
    private static string? StripPortalKey(string? portalRequest)
    {
        if (string.IsNullOrEmpty(portalRequest))
        {
            return portalRequest;
        }

        try
        {
            using var doc = JsonDocument.Parse(portalRequest);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("key", out _))
            {
                return portalRequest;
            }

            var withoutKey = doc.RootElement.EnumerateObject()
                .Where(p => !string.Equals(p.Name, "key", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(p => p.Name, p => p.Value);
            return JsonSerializer.Serialize(withoutKey);
        }
        catch (JsonException)
        {
            return portalRequest;
        }
    }
}
