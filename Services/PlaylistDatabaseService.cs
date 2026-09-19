using System;
using System.Globalization;
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


    Task DeleteAsync(int playlistId);


    Task<List<PlaylistDatabaseService.EpgAlias>> GetEpgAliasesAsync();


    Task UpsertEpgAliasesAsync(IReadOnlyList<PlaylistDatabaseService.EpgAlias> aliases);


    Task<List<PlaylistDatabaseService.ChannelOverride>> GetChannelOverridesAsync(int playlistId);


    Task UpsertChannelOverrideAsync(PlaylistDatabaseService.ChannelOverride overrideEntry);


    Task DeleteChannelOverridesAsync(int playlistId, IReadOnlyList<string> streamUrls);
}


public class PlaylistDatabaseService : IPlaylistCacheService
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer");

    private static readonly string DbPath = Path.Combine(CacheDirectory, "iptvplayer_cache.db");
    private static readonly string LegacyCacheFilePath = Path.Combine(CacheDirectory, "playlist_cache.json");

    private readonly ILogger<PlaylistDatabaseService> _logger;
    private Task? _initTask;

    public PlaylistDatabaseService(ILogger<PlaylistDatabaseService> logger)
    {
        _logger = logger;
    }

    // Lazy async schema init — keeps constructor off the UI thread
    private Task InitializeAsync()
    {
        _initTask ??= Task.Run(InitializeDatabase);
        return _initTask;
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
                CREATE INDEX IF NOT EXISTS idx_channels_playlist ON channels(playlist_id);
                CREATE TABLE IF NOT EXISTS epg_aliases (
                    key TEXT PRIMARY KEY,
                    xmltv_id TEXT NOT NULL,
                    xmltv_name TEXT NOT NULL,
                    stream_url TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS channel_overrides (
                    playlist_id INTEGER NOT NULL,
                    stream_url TEXT NOT NULL,
                    channel_name TEXT NOT NULL,
                    original_group TEXT,
                    tvg_id TEXT,
                    new_group TEXT,
                    is_deleted INTEGER NOT NULL DEFAULT 0,
                    created_at_utc TEXT NOT NULL,
                    PRIMARY KEY (playlist_id, stream_url)
                );";
            cmd.ExecuteNonQuery();


            try
            {
                var altCmd = connection.CreateCommand();
                altCmd.CommandText = "ALTER TABLE playlists ADD COLUMN portal_key TEXT";
                altCmd.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Column already exists
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось инициализировать SQLite БД.");
        }
    }

    // Load playlist cache from SQLite
    public async Task<PlaylistCache?> LoadAsync(int playlistId)
    {
        await InitializeAsync();
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

                var migrated = await TryMigrateFromJsonAsync(playlistId, connection);
                if (migrated != null) return migrated;
                return null;
            }

            var formatVersion = reader.GetInt32(0);
            var savedAtUtc = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
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

    // Save playlist cache to SQLite
    public async Task SaveAsync(int playlistId, PlaylistCache cache)
    {
        await InitializeAsync();
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            await connection.OpenAsync();

            await using var transaction = await connection.BeginTransactionAsync();


            var metaCmd = connection.CreateCommand();
            metaCmd.CommandText =
                "INSERT INTO playlists (id, format_version, saved_at_utc, portal_key) VALUES ($id, $ver, $date, $key) " +
                "ON CONFLICT(id) DO UPDATE SET format_version = $ver, saved_at_utc = $date, portal_key = $key";
            metaCmd.Parameters.AddWithValue("$id", playlistId);
            metaCmd.Parameters.AddWithValue("$ver", cache.FormatVersion);
            metaCmd.Parameters.AddWithValue("$date", cache.SavedAtUtc.ToString("O"));
            metaCmd.Parameters.AddWithValue("$key", (object?)cache.PortalKeyHash ?? DBNull.Value);
            await metaCmd.ExecuteNonQueryAsync();


            var deleteCmd = connection.CreateCommand();
            deleteCmd.CommandText = "DELETE FROM channels WHERE playlist_id = $id";
            deleteCmd.Parameters.AddWithValue("$id", playlistId);
            await deleteCmd.ExecuteNonQueryAsync();


            // Prepared command reused for all rows
            var insertCmd = connection.CreateCommand();
            insertCmd.CommandText =
                "INSERT INTO channels (playlist_id, name, stream_url, logo_url, \"group\", tvg_id, catchup_days, portal_request, description, year, genre) " +
                "VALUES ($pid, $name, $url, $logo, $group, $tvg, $catchup, $portal, $desc, $year, $genre)";
            var pPid = insertCmd.Parameters.Add("$pid", SqliteType.Integer);
            var pName = insertCmd.Parameters.Add("$name", SqliteType.Text);
            var pUrl = insertCmd.Parameters.Add("$url", SqliteType.Text);
            var pLogo = insertCmd.Parameters.Add("$logo", SqliteType.Text);
            var pGroup = insertCmd.Parameters.Add("$group", SqliteType.Text);
            var pTvg = insertCmd.Parameters.Add("$tvg", SqliteType.Text);
            var pCatchup = insertCmd.Parameters.Add("$catchup", SqliteType.Integer);
            var pPortal = insertCmd.Parameters.Add("$portal", SqliteType.Text);
            var pDesc = insertCmd.Parameters.Add("$desc", SqliteType.Text);
            var pYear = insertCmd.Parameters.Add("$year", SqliteType.Integer);
            var pGenre = insertCmd.Parameters.Add("$genre", SqliteType.Text);
            pPid.Value = playlistId;

            foreach (var ch in cache.Channels)
            {
                pName.Value = ch.Name;
                pUrl.Value = (object?)ch.StreamUrl ?? DBNull.Value;
                pLogo.Value = (object?)ch.LogoUrl ?? DBNull.Value;
                pGroup.Value = (object?)ch.Group ?? DBNull.Value;
                pTvg.Value = (object?)ch.TvgId ?? DBNull.Value;
                pCatchup.Value = ch.CatchupDays;
                pPortal.Value = (object?)StripPortalKey(ch.PortalRequest) ?? DBNull.Value;
                pDesc.Value = (object?)ch.Description ?? DBNull.Value;
                pYear.Value = ch.Year;
                pGenre.Value = (object?)ch.Genre ?? DBNull.Value;
                await insertCmd.ExecuteNonQueryAsync();
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

    // Delete playlist cache
    public async Task DeleteAsync(int playlistId)
    {
        await InitializeAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            await connection.OpenAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            foreach (var table in new[] { "channels", "channel_overrides" })
            {
                var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"DELETE FROM {table} WHERE playlist_id = $id";
                cmd.Parameters.AddWithValue("$id", playlistId);
                await cmd.ExecuteNonQueryAsync();
            }

            var metaCmd = connection.CreateCommand();
            metaCmd.Transaction = transaction;
            metaCmd.CommandText = "DELETE FROM playlists WHERE id = $id";
            metaCmd.Parameters.AddWithValue("$id", playlistId);
            await metaCmd.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить кэш плейлиста {PlaylistId} из SQLite.", playlistId);
        }
    }


    private async Task<PlaylistCache?> TryMigrateFromJsonAsync(int playlistId, SqliteConnection connection)
    {
        var path = playlistId == 1 && !File.Exists(LegacyCacheFilePath)
            ? null
            : Path.Combine(CacheDirectory, $"playlist_cache_{playlistId}.json");


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
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return portalRequest;
        }
    }


    public sealed record EpgAlias(string Key, string XmlTvId, string StreamUrl);

    // Load learned EPG aliases
    public async Task<List<EpgAlias>> GetEpgAliasesAsync()
    {
        await InitializeAsync();
        var result = new List<EpgAlias>();
        try
        {
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            await connection.OpenAsync();

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT key, xmltv_id, stream_url FROM epg_aliases";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new EpgAlias(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать таблицу EPG-псевдонимов.");
        }

        return result;
    }


    // Save learned EPG aliases
    public async Task UpsertEpgAliasesAsync(IReadOnlyList<EpgAlias> aliases)
    {
        await InitializeAsync();
        if (aliases.Count == 0)
        {
            return;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            await connection.OpenAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT OR REPLACE INTO epg_aliases(key, xmltv_id, xmltv_name, stream_url, created_at_utc)
                VALUES ($key, $xmltvId, $name, $streamUrl, $now)";
            var keyParam = cmd.Parameters.Add("$key", SqliteType.Text);
            var idParam = cmd.Parameters.Add("$xmltvId", SqliteType.Text);
            var nameParam = cmd.Parameters.Add("$name", SqliteType.Text);
            var urlParam = cmd.Parameters.Add("$streamUrl", SqliteType.Text);
            var nowParam = cmd.Parameters.Add("$now", SqliteType.Text);

            foreach (var alias in aliases)
            {
                keyParam.Value = alias.Key;
                idParam.Value = alias.XmlTvId;
                nameParam.Value = alias.Key;
                urlParam.Value = alias.StreamUrl;
                nowParam.Value = DateTime.UtcNow.ToString("O");
                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось сохранить EPG-псевдонимы ({Count} шт.).", aliases.Count);
        }
    }


    public sealed record ChannelOverride(
        int PlaylistId,
        string StreamUrl,
        string ChannelName,
        string? OriginalGroup,
        string? TvgId,
        string? NewGroup,
        bool IsDeleted,
        DateTime CreatedAtUtc);

    // Load channel move/remove overrides
    public async Task<List<ChannelOverride>> GetChannelOverridesAsync(int playlistId)
    {
        await InitializeAsync();
        var result = new List<ChannelOverride>();
        try
        {
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            await connection.OpenAsync();

            var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT stream_url, channel_name, original_group, tvg_id, new_group, is_deleted, created_at_utc " +
                "FROM channel_overrides WHERE playlist_id = $id";
            cmd.Parameters.AddWithValue("$id", playlistId);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new ChannelOverride(
                    playlistId,
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt32(5) != 0,
                    DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать правки каналов плейлиста {PlaylistId}.", playlistId);
        }

        return result;
    }

    // Save single channel override
    public async Task UpsertChannelOverrideAsync(ChannelOverride overrideEntry)
    {
        await InitializeAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            await connection.OpenAsync();

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO channel_overrides
                    (playlist_id, stream_url, channel_name, original_group, tvg_id, new_group, is_deleted, created_at_utc)
                VALUES ($pid, $url, $name, $origGroup, $tvg, $newGroup, $deleted, $now)";
            cmd.Parameters.AddWithValue("$pid", overrideEntry.PlaylistId);
            cmd.Parameters.AddWithValue("$url", overrideEntry.StreamUrl);
            cmd.Parameters.AddWithValue("$name", overrideEntry.ChannelName);
            cmd.Parameters.AddWithValue("$origGroup", (object?)overrideEntry.OriginalGroup ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tvg", (object?)overrideEntry.TvgId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$newGroup", (object?)overrideEntry.NewGroup ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$deleted", overrideEntry.IsDeleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось сохранить правку канала «{Channel}».", overrideEntry.ChannelName);
        }
    }

    // Delete channel overrides by URL
    public async Task DeleteChannelOverridesAsync(int playlistId, IReadOnlyList<string> streamUrls)
    {
        await InitializeAsync();
        if (streamUrls.Count == 0)
        {
            return;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            await connection.OpenAsync();

            var cmd = connection.CreateCommand();
            var urlParams = string.Join(", ", streamUrls.Select((_, i) => $"$u{i}"));
            cmd.CommandText =
                $"DELETE FROM channel_overrides WHERE playlist_id = $pid AND stream_url IN ({urlParams})";
            cmd.Parameters.AddWithValue("$pid", playlistId);
            for (var i = 0; i < streamUrls.Count; i++)
            {
                cmd.Parameters.AddWithValue($"$u{i}", streamUrls[i]);
            }
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить правки каналов плейлиста {PlaylistId}.", playlistId);
        }
    }
}
