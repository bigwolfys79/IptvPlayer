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


public sealed partial class MainPage : Page
{


    internal static string DefaultPlaylistName(string url)
    {

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


            List<ChannelViewModel> playlistChannels;
            if (playlist.IsPortal)
            {
                var items = await _videoPortalService.LoadCatalogAsync(playlist, ct);
                playlistChannels = items.Select(PortalItemToChannel).ToList();


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


    private async Task<List<ChannelViewModel>> LoadPlaylistChannelsWithOverlayAsync(
        PlaylistSource playlist, System.Threading.CancellationToken ct = default)
    {
        ViewModel.PlaylistLoadingText = string.Format(
            L.T(playlist.IsPortal ? "Zagruzka_Kataloga_Portala_Nazvanie" : "Zagruzka_Pleylista_Nazvanie"),
            playlist.Name);
        ViewModel.IsPlaylistLoading = true;
        var startedAt = Environment.TickCount64;
        try
        {
            return await LoadPlaylistChannelsAsync(playlist, ct);
        }
        finally
        {
            var remainMs = MinOverlayDisplayMs - (Environment.TickCount64 - startedAt);
            if (remainMs > 0)
            {
                await Task.Delay((int)remainMs, CancellationToken.None);
            }
            ViewModel.IsPlaylistLoading = false;
        }
    }

    private const int MinOverlayDisplayMs = 2000;

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


    private void ResetFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetPortalFilters();
    }


    private static bool IsCacheDue(DateTime savedAtUtc, int refreshDays)
    {
        if (refreshDays <= 0)
        {
            return false;
        }

        if (savedAtUtc == default)
        {
            return true;
        }

        return (DateTime.UtcNow - savedAtUtc) >= TimeSpan.FromDays(refreshDays);
    }


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
