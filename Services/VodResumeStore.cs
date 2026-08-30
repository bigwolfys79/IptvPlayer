using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IptvPlayer.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;

/// <summary>
/// Позиции досмотра фильмов/серий портала в SQLite (та же
/// iptvplayer_cache.db, что у кэша плейлистов). Вынесено из settings.json:
/// записи обновляются каждые несколько секунд во время просмотра — так
/// настройки не переписываются целиком, а позиции переживают перезапуск.
/// Машинно-зависимые данные: в экспорт настроек не попадают.
/// </summary>
public class VodResumeStore
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer");

    private static readonly string DbPath = Path.Combine(CacheDirectory, "iptvplayer_cache.db");

    private readonly ILogger<VodResumeStore> _logger;

    public VodResumeStore(ILogger<VodResumeStore> logger)
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
                CREATE TABLE IF NOT EXISTS vod_resume (
                    key TEXT PRIMARY KEY,
                    position_seconds REAL NOT NULL,
                    duration_seconds REAL NOT NULL,
                    episode_index INTEGER NOT NULL,
                    updated_at TEXT NOT NULL,
                    portal_playlist_id INTEGER
                );";
            cmd.ExecuteNonQuery();

            // Миграция: добавляем portal_playlist_id если таблица уже существует без него.
            try
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE vod_resume ADD COLUMN portal_playlist_id INTEGER";
                alter.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Колонка уже существует — нормально.
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось инициализировать таблицу vod_resume.");
        }
    }

    /// <summary>Загружает все сохранённые позиции (пустой словарь при сбое).</summary>
    public async Task<Dictionary<string, VodResumePosition>> LoadAllAsync()
    {
        var result = new Dictionary<string, VodResumePosition>();
        try
        {
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            await connection.OpenAsync();

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT key, position_seconds, duration_seconds, episode_index, updated_at, portal_playlist_id
                FROM vod_resume";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var position = new VodResumePosition
                {
                    PositionSeconds = reader.GetDouble(1),
                    DurationSeconds = reader.GetDouble(2),
                    EpisodeIndex = reader.GetInt32(3),
                    UpdatedAt = DateTime.TryParse(reader.GetString(4), out var at) ? at : DateTime.Now,
                    PortalPlaylistId = reader.IsDBNull(5) ? null : reader.GetInt32(5)
                };
                result[reader.GetString(0)] = position;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось загрузить позиции просмотра из SQLite.");
        }

        return result;
    }

    /// <summary>Сохраняет весь текущий набор позиций (upsert) одной транзакцией.</summary>
    public async Task SaveAllAsync(IReadOnlyDictionary<string, VodResumePosition> positions)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            await connection.OpenAsync();

            using var transaction = await connection.BeginTransactionAsync();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)transaction;
                cmd.CommandText = @"
                    INSERT INTO vod_resume (key, position_seconds, duration_seconds, episode_index, updated_at, portal_playlist_id)
                    VALUES ($key, $pos, $dur, $ep, $at, $pid)
                    ON CONFLICT(key) DO UPDATE SET
                        position_seconds = $pos, duration_seconds = $dur,
                        episode_index = $ep, updated_at = $at, portal_playlist_id = $pid";
                var key = cmd.Parameters.Add("$key", SqliteType.Text);
                var pos = cmd.Parameters.Add("$pos", SqliteType.Real);
                var dur = cmd.Parameters.Add("$dur", SqliteType.Real);
                var ep = cmd.Parameters.Add("$ep", SqliteType.Integer);
                var at = cmd.Parameters.Add("$at", SqliteType.Text);
                var pid = cmd.Parameters.Add("$pid", SqliteType.Integer);

                foreach (var (k, p) in positions)
                {
                    key.Value = k;
                    pos.Value = p.PositionSeconds;
                    dur.Value = p.DurationSeconds;
                    ep.Value = p.EpisodeIndex;
                    at.Value = p.UpdatedAt.ToString("O");
                    pid.Value = (object?)p.PortalPlaylistId ?? DBNull.Value;
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // Удаляем записи, исчезнувшие из словаря (прунинг во ViewModel).
            var keys = positions.Keys.ToArray();
            var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = $@"
                DELETE FROM vod_resume
                WHERE key NOT IN ({string.Join(",", keys.Select((_, i) => $"$k{i}"))})";
            for (var i = 0; i < keys.Length; i++)
            {
                delete.Parameters.Add($"$k{i}", SqliteType.Text).Value = keys[i];
            }

            if (keys.Length > 0)
            {
                await delete.ExecuteNonQueryAsync();
            }
            else
            {
                delete.CommandText = "DELETE FROM vod_resume";
                delete.Parameters.Clear();
                await delete.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить позиции просмотра в SQLite.");
        }
    }
}
