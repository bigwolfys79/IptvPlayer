using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using IptvPlayer.Models;
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
/// Хранит кэш разобранного плейлиста в %LocalAppData%\IptvPlayer\
/// playlist_cache_{playlistId}.json — по файлу на каждый плейлист из
/// AppSettings.Playlists. Тот же базовый каталог, что и кэш CacheService,
/// но вне его подпапки "cache" (стирается CacheService.Clear при "Обновить
/// EPG"). Ошибки диска глотаются с записью в лог: отсутствие кэша не должно
/// ронять запуск — плейлист просто скачается заново.
///
/// Миграция: до поддержки нескольких плейлистов кэш жил в единственном
/// playlist_cache.json. При первом чтении плейлиста с Id=1, если нового файла
/// нет, старый файл переименовывается в playlist_cache_1.json.
/// </summary>
public class PlaylistCacheService : IPlaylistCacheService
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer");

    private static readonly string LegacyCacheFilePath = Path.Combine(
        CacheDirectory, "playlist_cache.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ILogger<PlaylistCacheService> _logger;

    public PlaylistCacheService(ILogger<PlaylistCacheService> logger)
    {
        _logger = logger;
    }

    private static string CacheFilePath(int playlistId) =>
        Path.Combine(CacheDirectory, $"playlist_cache_{playlistId}.json");

    public async Task<PlaylistCache?> LoadAsync(int playlistId)
    {
        try
        {
            var path = CacheFilePath(playlistId);

            // Разовая миграция единственного старого кэша в кэш плейлиста 1 —
            // плейлист, добавленный из устаревшего PlaylistUrl, получает Id=1.
            if (!File.Exists(path) && playlistId == 1 && File.Exists(LegacyCacheFilePath))
            {
                File.Move(LegacyCacheFilePath, path);
                _logger.LogInformation(
                    "Кэш плейлиста перенесён из playlist_cache.json в playlist_cache_1.json.");
            }

            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<PlaylistCache>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Кэш плейлиста {PlaylistId} не читается — будет перекачан.", playlistId);
            return null;
        }
    }

    public async Task SaveAsync(int playlistId, PlaylistCache cache)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllTextAsync(CacheFilePath(playlistId), JsonSerializer.Serialize(cache, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить кэш плейлиста {PlaylistId}.", playlistId);
        }
    }

    public Task DeleteAsync(int playlistId)
    {
        try
        {
            var path = CacheFilePath(playlistId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить кэш плейлиста {PlaylistId}.", playlistId);
        }
        return Task.CompletedTask;
    }
}
