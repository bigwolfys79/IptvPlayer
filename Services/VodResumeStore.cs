using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IptvPlayer.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;


public class VodResumeStore
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer");

    private static readonly string DbPath = Path.Combine(CacheDirectory, "iptvplayer_cache.db");

    private readonly ILogger<VodResumeStore> _logger;

    private readonly SemaphoreSlim _saveGate = new(1, 1);

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


            try
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE vod_resume ADD COLUMN portal_playlist_id INTEGER";
                alter.ExecuteNonQuery();
            }
            catch (SqliteException)
            {

            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось инициализировать таблицу vod_resume.");
        }
    }


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


    public async Task SaveAllAsync(IReadOnlyDictionary<string, VodResumePosition> positions)
    {
        // Defensive snapshot before first await: caller may mutate the live dictionary
        var snapshot = new Dictionary<string, VodResumePosition>(positions);

        if (snapshot.Count == 0)
        {
            // Empty in-memory state is transient (list not loaded yet) — never wipe stored positions
            return;
        }

        await _saveGate.WaitAsync();
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

                foreach (var (k, p) in snapshot)
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


            var keys = snapshot.Keys.ToArray();
            var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = $@"
                DELETE FROM vod_resume
                WHERE key NOT IN ({string.Join(",", keys.Select((_, i) => $"$k{i}"))})";
            for (var i = 0; i < keys.Length; i++)
            {
                delete.Parameters.Add($"$k{i}", SqliteType.Text).Value = keys[i];
            }

            await delete.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить позиции просмотра в SQLite.");
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
