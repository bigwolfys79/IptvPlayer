using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.ViewModels;

/// <summary>
/// VOD resume positions and portal playback (PlayChannelAsync interactive).
/// </summary>
public partial class MainPageViewModel
{
    private const int MaxVodResumeEntries = 200;
    private const double MinVodResumeSeconds = 30;
    private const double VodResumeWatchedFraction = 0.95;

    private DateTime _lastVodResumeSaveRequest = DateTime.MinValue;

    public event Func<string, TimeSpan, Task<bool>>? VodResumePromptRequested;

    internal static string VodResumeKey(string title, int episodeIndex)
        => episodeIndex >= 0 ? $"{title}::{episodeIndex}" : title;

    public async Task LoadVodResumePositionsAsync()
    {
        var stored = await _vodResumeStore.LoadAllAsync();
        foreach (var (key, position) in stored)
        {
            _vodResumePositions[key] = position;
        }

        if (AppSettings.VodResumePositions.Count == 0)
        {
            return;
        }

        foreach (var (key, position) in AppSettings.VodResumePositions)
        {
            _vodResumePositions[key] = position;
        }

        AppSettings.VodResumePositions.Clear();
        try
        {
            await _settingsService.SaveAsync(AppSettings);
            await _vodResumeStore.SaveAllAsync(_vodResumePositions);
            _logger.LogInformation("Позиции просмотра перенесены из settings.json в БД ({Count} шт.).",
                _vodResumePositions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Миграция позиций просмотра в БД не удалась.");
        }
    }

    public TimeSpan? GetSavedVodPosition(string title, int episodeIndex)
    {
        if (_vodResumePositions.TryGetValue(VodResumeKey(title, episodeIndex),
                out var entry) && entry.PositionSeconds >= MinVodResumeSeconds &&
            (entry.DurationSeconds <= 0 ||
             entry.PositionSeconds <= entry.DurationSeconds * VodResumeWatchedFraction))
        {
            return TimeSpan.FromSeconds(entry.PositionSeconds);
        }

        return null;
    }

    public async Task<TimeSpan?> OfferVodResumeAsync(string title, int episodeIndex)
    {
        var saved = GetSavedVodPosition(title, episodeIndex);
        if (saved == null || VodResumePromptRequested == null)
        {
            return null;
        }

        return await VodResumePromptRequested(title, saved.Value) ? saved : null;
    }

    public void CaptureVodPosition()
    {
        if (!Player.IsVodPlaying || Player.VodChannel is not { } channel ||
            string.IsNullOrWhiteSpace(channel.Name))
        {
            return;
        }

        var position = Player.VodPositionSeconds;
        if (position < MinVodResumeSeconds)
        {
            _vodResumePositions.Remove(VodResumeKey(channel.Name, Player.CurrentVodEpisodeIndex));
            return;
        }

        var activePlaylist = AppSettings.Playlists.FirstOrDefault(p => p.Id == AppSettings.ActivePlaylistId);
        _vodResumePositions[VodResumeKey(channel.Name, Player.CurrentVodEpisodeIndex)] =
            new VodResumePosition
            {
                PositionSeconds = position,
                DurationSeconds = Player.VodDurationSeconds,
                EpisodeIndex = Player.CurrentVodEpisodeIndex,
                UpdatedAt = DateTime.Now,
                PortalPlaylistId = activePlaylist?.Id
            };

        PruneVodResumeEntries();

        if ((DateTime.Now - _lastVodResumeSaveRequest).TotalSeconds >= 5)
        {
            _lastVodResumeSaveRequest = DateTime.Now;
            _ = _vodResumeStore.SaveAllAsync(_vodResumePositions);
        }
    }

    private void PruneVodResumeEntries()
    {
        var positions = _vodResumePositions;
        var finished = positions.Where(kv => kv.Value.DurationSeconds > 0 &&
                                             kv.Value.PositionSeconds > kv.Value.DurationSeconds * VodResumeWatchedFraction)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in finished)
        {
            positions.Remove(key);
        }

        while (positions.Count > MaxVodResumeEntries)
        {
            var oldest = positions.OrderBy(kv => kv.Value.UpdatedAt).First().Key;
            positions.Remove(oldest);
        }
    }

    public async Task<bool> PlayChannelAsync(ChannelViewModel channel, bool interactive)
    {
        if (!string.IsNullOrWhiteSpace(channel.PortalRequest))
        {
            var playlist = AppSettings.Playlists.FirstOrDefault(p => p.Id == AppSettings.ActivePlaylistId);
            if (playlist == null)
            {
                Player.StreamError = L.T("Istochnik_Portala_Ne_Nayden");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(channel.StreamUrl))
            {
                var catalogResume = interactive
                    ? await OfferVodResumeAsync(channel.Name, -1)
                    : null;
                await Player.StartPlaybackAsync(channel, channel.StreamUrl!, archiveEntry: null,
                    isVod: true, resumePosition: catalogResume);
                if (!string.IsNullOrWhiteSpace(channel.PortalRequest))
                {
                    _ = LoadPortalVariantsInBackgroundAsync(playlist, channel);
                }

                return true;
            }

            PortalFlickResult flick;
            Player.IsBuffering = true;
            try
            {
                flick = await _videoPortalService.ResolveEpisodesAsync(playlist, channel.PortalRequest);
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrWhiteSpace(channel.StreamUrl))
                {
                    _logger.LogWarning(ex,
                        "Портал: flick для «{Item}» не удался, используется ссылка из каталога.", channel.Name);
                    await Player.StartPlaybackAsync(channel, channel.StreamUrl, archiveEntry: null, isVod: true);
                    return true;
                }

                _logger.LogError(ex, "Портал: не удалось получить поток для «{Item}».", channel.Name);
                Player.StreamError = string.Format(L.T("Portal_Ne_Otdal_Potok_0"), ex.Message, ex.Message);
                return false;
            }
            finally
            {
                Player.IsBuffering = false;
            }

            var episode = flick.Episodes[0];
            _logger.LogInformation(
                "Портал: «{Name}» — эпизодов {Count}, interactive={Interactive}, подписчиков выбора {Subscribers}.",
                channel.Name, flick.Episodes.Count, interactive, PortalEpisodePickRequested?.GetInvocationList().Length ?? 0);
            if (flick.Episodes.Count > 1 && interactive && PortalEpisodePickRequested is { } pick)
            {
                var chosen = await pick(channel, flick);
                if (chosen is not { } picked)
                {
                    return false;
                }

                channel = picked.Channel;
                flick = new PortalFlickResult
                {
                    SerialTitle = picked.Channel.Name,
                    Description = picked.Channel.Description,
                    PosterUrl = picked.Channel.LogoUrl,
                    Episodes = picked.Episodes
                };
                episode = picked.Episode;
            }

            var preferred = AppSettings.PreferredQuality > 0 ? AppSettings.PreferredQuality + "p" : "Авто";
            var quality = episode.Variants.Count > 0 ? preferred : null;

            var episodeResume = interactive
                ? await OfferVodResumeAsync(channel.Name, flick.Episodes.IndexOf(episode))
                : null;
            await Player.StartPlaybackAsync(channel, episode.StreamUrl, archiveEntry: null, isVod: true,
                vodVariants: episode.Variants, vodQuality: quality,
                resumePosition: episodeResume,
                vodEpisodes: flick.Episodes, vodEpisodeIndex: flick.Episodes.IndexOf(episode));
            return true;
        }

        await Player.PlayLiveAsync(channel);
        return !string.IsNullOrWhiteSpace(channel.StreamUrl);
    }

    private async Task LoadPortalVariantsInBackgroundAsync(PlaylistSource playlist, ChannelViewModel channel)
    {
        try
        {
            var flick = await _videoPortalService.ResolveEpisodesAsync(playlist, channel.PortalRequest!);
            if (Player.IsVodPlaying && ReferenceEquals(Player.VodChannel, channel) &&
                flick.Episodes.Count > 0)
            {
                Player.SetVodVariants(flick.Episodes[0].Variants);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Фоновая догрузка вариантов качества для «{Item}» не удалась.", channel.Name);
        }
    }

    public event Func<ChannelViewModel, PortalFlickResult, Task<(ChannelViewModel Channel, PortalEpisode Episode, List<PortalEpisode> Episodes)?> >? PortalEpisodePickRequested;
}
