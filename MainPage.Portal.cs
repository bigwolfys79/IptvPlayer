using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer;

/// <summary>
/// Portal-related methods: catalog loading, cache management, episode picking.
/// </summary>
public sealed partial class MainPage : Page
{
    /// <summary>
    /// Имя плейлиста по умолчанию — хост URL (без www), чтобы список плейлистов
    /// был узнаваемым без обязательного ввода имени при добавлении.
    /// </summary>
    internal static string DefaultPlaylistName(string url)
    {
        // Локальный файл плейлиста — имя по файлу без расширения.
        if (System.IO.File.Exists(url))
        {
            return System.IO.Path.GetFileNameWithoutExtension(url);
        }

        try
        {
            var host = new Uri(url).Host;
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? host[4..]
                : host;
        }
        catch (Exception ex)
        {
            Serilog.Log.Information(ex, "Не удалось извлечь хост из URL плейлиста — показываем исходный URL.");
            return url;
        }
    }

    /// <summary>
    /// Загружает каналы плейлиста при старте и при переключении: если кэш
    /// этого плейлиста свеж (PlaylistRefreshDays не истёк и формат актуален) —
    /// каналы берутся из кэша без скачивания; иначе M3U перекачивается и кэш
    /// обновляется. При сбое скачивания отдаётся пусть и просроченный кэш —
    /// переключение/запуск не должно оставлять пользователя без каналов.
    /// </summary>
    private async Task<List<ChannelViewModel>> LoadPlaylistChannelsAsync(
        PlaylistSource playlist, System.Threading.CancellationToken ct = default)
    {
        var result = new List<ChannelViewModel>();
        var playlistCache = await _playlistCacheService.LoadAsync(playlist.Id);
        var keyHash = string.IsNullOrEmpty(playlist.PortalKey) ? null : ComputeKeyHash(playlist.PortalKey);
        var refreshDue = playlistCache == null ||
                         playlistCache.Channels.Count == 0 ||
                         playlistCache.FormatVersion < PlaylistCache.CurrentFormatVersion ||
                         IsCacheDue(playlistCache.SavedAtUtc, ViewModel.AppSettings.PlaylistRefreshDays) ||
                         (playlist.IsPortal && playlistCache.PortalKeyHash != null && playlistCache.PortalKeyHash != keyHash);

        if (!refreshDue && playlistCache != null)
        {
            foreach (var cached in playlistCache.Channels)
            {
                result.Add(CachedToChannel(cached));
            }

            _logger.LogInformation(
                "Плейлист {Playlist} взят из локального кэша (возраст {Age:F1} ч) — скачивание пропущено.",
                playlist.Name, (DateTime.UtcNow - playlistCache.SavedAtUtc).TotalHours);

            // Для портала: загружаем жанры/категории из manifest даже при
            // использовании кэша — иначе комбобоксы фильтров пустые.
            // Для M3U: очищаем портальные фильтры (могли остаться от предыдущего плейлиста).
            if (playlist.IsPortal)
            {
                try
                {
                    var (genres, years, categories) = await _videoPortalService.LoadManifestInfoAsync(playlist, ct);
                    ViewModel.SetPortalInfo(playlist, genres, years, categories);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось загрузить manifest info из кэша.");
                    ViewModel.ClearPortalInfo();
                }
            }
            else
            {
                ViewModel.ClearPortalInfo();
            }

            return result;
        }

        try
        {
            // Портал-источник: вместо M3U — каталог видео-портала (manifest →
            // категории → элементы). Локальный файл — только для M3U.
            List<ChannelViewModel> playlistChannels;
            if (playlist.IsPortal)
            {
                var items = await _videoPortalService.LoadCatalogAsync(playlist, ct);
                playlistChannels = items.Select(PortalItemToChannel).ToList();

                // Загружаем жанры и категории из manifest для серверных фильтров.
                try
                {
                    var (genres, years, categories) = await _videoPortalService.LoadManifestInfoAsync(playlist, ct);
                    ViewModel.SetPortalInfo(playlist, genres, years, categories);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось загрузить manifest info для серверных фильтров.");
                    ViewModel.ClearPortalInfo();
                }
            }
            else
            {
                playlistChannels = System.IO.File.Exists(playlist.Url)
                    ? await _m3uParserService.ParseFromFileAsync(playlist.Url)
                    : await _m3uParserService.ParseFromUrlAsync(playlist.Url, ct);
                ViewModel.ClearPortalInfo();
            }

            result.AddRange(playlistChannels);
            await SavePlaylistCacheAsync(playlist.Id, playlistChannels, playlist.PortalKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось загрузить плейлист {Playlist} ({Url}).", playlist.Name, playlist.Url);

            if (playlistCache != null)
            {
                foreach (var cached in playlistCache.Channels)
                {
                    result.Add(CachedToChannel(cached));
                }
            }
        }

        return result;
    }

    private static ChannelViewModel CachedToChannel(Models.CachedChannel cached) => new()
    {
        Name = cached.Name,
        StreamUrl = cached.StreamUrl,
        LogoUrl = cached.LogoUrl,
        Group = cached.Group,
        TvgId = cached.TvgId,
        CatchupDays = cached.CatchupDays,
        PortalRequest = cached.PortalRequest,
        Description = cached.Description,
        Year = cached.Year,
        Genre = cached.Genre
    };

    /// <summary>
    /// Элемент каталога портала → канал: категория становится группой
    /// (фильтр групп работает без изменений), StreamUrl остаётся null до
    /// клика — поток у портала одноразовый и запрашивается по клику.
    /// </summary>
    private static ChannelViewModel PortalItemToChannel(PortalCatalogItem item) => new()
    {
        Name = item.Name,
        Group = item.Group,
        LogoUrl = item.LogoUrl,
        StreamUrl = item.StreamUrl,
        PortalRequest = item.RequestJson,
        Description = item.Description,
        Year = item.Year,
        Genre = item.Genre
    };

    private Task<(ChannelViewModel Channel, PortalEpisode Episode, List<PortalEpisode> Episodes)?> OnPortalEpisodePickRequested(ChannelViewModel channel, PortalFlickResult flick)
    {
        var completion = new TaskCompletionSource<(ChannelViewModel Channel, PortalEpisode Episode, List<PortalEpisode> Episodes)?>();
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await Dialogs.EpisodePickerDialog.PickAsync(Content.XamlRoot, channel, flick));
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Диалог выбора серии не показался.");
                completion.SetResult(null);
            }
        });
        return completion.Task;
    }

    /// <summary>
    /// Кнопка сброса фильтров портала: возвращает «Все типы / Все жанры / Все годы»
    /// и запускает одну серверную перезагрузку каталога с итоговым состоянием.
    /// </summary>
    private void ResetFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetPortalFilters();
    }

    /// <summary>
    /// Проверяет, наступил ли срок обновления кэша по настройкам
    /// (AppSettings.PlaylistRefreshDays / EpgRefreshDays).
    /// savedAtUtc — момент последнего сохранения кэша (UTC).
    /// refreshDays — количество дней из настроек (0 = никогда не обновлять).
    /// </summary>
    private static bool IsCacheDue(DateTime savedAtUtc, int refreshDays)
    {
        if (refreshDays <= 0)
        {
            return false; // "Никогда" — кэш всегда считается свежим
        }

        if (savedAtUtc == default)
        {
            return true; // Нет метки — считаем просроченным
        }

        return (DateTime.UtcNow - savedAtUtc) >= TimeSpan.FromDays(refreshDays);
    }

    /// <summary>
    /// Сохраняет разобранные каналы плейлиста в локальный кэш SQLite
    /// (PlaylistDatabaseService) — при следующем запуске, если срок
    /// обновления из настроек ещё не наступил, плейлист не придётся
    /// перекачивать.
    /// </summary>
    private Task SavePlaylistCacheAsync(int playlistId, List<ChannelViewModel> channels, string? portalKey = null)
    {
        var cache = new Models.PlaylistCache
        {
            FormatVersion = Models.PlaylistCache.CurrentFormatVersion,
            SavedAtUtc = DateTime.UtcNow,
            PortalKeyHash = string.IsNullOrEmpty(portalKey) ? null : ComputeKeyHash(portalKey),
            Channels = channels.Select(c => new Models.CachedChannel
            {
                Name = c.Name,
                StreamUrl = c.StreamUrl,
                LogoUrl = c.LogoUrl,
                Group = c.Group,
                TvgId = c.TvgId,
                CatchupDays = c.CatchupDays,
                PortalRequest = c.PortalRequest,
                Description = c.Description,
                Year = c.Year,
                Genre = c.Genre
            }).ToList()
        };

        return _playlistCacheService.SaveAsync(playlistId, cache);
    }

    private static string ComputeKeyHash(string key)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes);
    }
}
