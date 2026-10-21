using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.ViewModels;


public partial class MainPageViewModel
{
    private const int MaxVodResumeEntries = 200;
    private const double MinVodResumeSeconds = 30;
    private const double VodResumeWatchedFraction = 0.95;

    private DateTime _lastVodResumeSaveRequest = DateTime.MinValue;

    public event Func<string, TimeSpan, Task<bool>>? VodResumePromptRequested;

    // Series resume: (title, seasonIndex, episodeIndex, position) — asked
    // before the tree jumps to the saved leaf; false means "start over"
    public event Func<string, int, int, TimeSpan, Task<bool>>? OcSeriesResumePromptRequested;

    internal static string VodResumeKey(string title, int seasonIndex, int episodeIndex)
        => seasonIndex >= 0 && episodeIndex >= 0
            ? $"{title}::s{seasonIndex}e{episodeIndex}"
            : episodeIndex >= 0 ? $"{title}::{episodeIndex}" : title;


    internal static string LocalFileResumeKey(string path) => $"file::{path}";

    public TimeSpan? GetSavedLocalFilePosition(string path)
    {
        if (_vodResumePositions.TryGetValue(LocalFileResumeKey(path),
                out var entry) && entry.PositionSeconds >= MinVodResumeSeconds &&
            (entry.DurationSeconds <= 0 ||
             entry.PositionSeconds <= entry.DurationSeconds * VodResumeWatchedFraction))
        {
            return TimeSpan.FromSeconds(entry.PositionSeconds);
        }

        return null;
    }

    public async Task<TimeSpan?> OfferLocalFileResumeAsync(string path, string title)
    {
        var saved = GetSavedLocalFilePosition(path);
        if (saved == null || VodResumePromptRequested == null)
        {
            return null;
        }

        return await VodResumePromptRequested(title, saved.Value) ? saved : null;
    }


    public Task FlushVodResumePositionsAsync()
    {
        // Snapshot: live dictionary may be mutated by UI while save runs on a pool thread
        return _vodResumeStore.SaveAllAsync(
            new Dictionary<string, VodResumePosition>(_vodResumePositions));
    }

    public async Task LoadVodResumePositionsAsync()
    {
        var stored = await _vodResumeStore.LoadAllAsync();

        // Film entries captured with stale season indexes (::s0e0 on the key,
        // -1 in the fields) re-key to the flat title — the film lookup never
        // sees season keys
        foreach (var key in stored.Keys.ToList())
        {
            if (key.EndsWith("::s0e0", StringComparison.Ordinal) && stored[key].OcSeasonIndex < 0)
            {
                var title = key[..^6];
                if (!stored.ContainsKey(title))
                {
                    stored[title] = stored[key];
                }

                stored.Remove(key);
            }
        }

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
            // Snapshot: avoid enumerating live dictionary concurrently with UI mutations
            await _vodResumeStore.SaveAllAsync(
                new Dictionary<string, VodResumePosition>(_vodResumePositions));
            _logger.LogInformation("Позиции просмотра перенесены из settings.json в БД ({Count} шт.).",
                _vodResumePositions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Миграция позиций просмотра в БД не удалась.");
        }
    }

    public TimeSpan? GetSavedVodPosition(string title, int episodeIndex)
        => GetSavedVodPosition(title, -1, episodeIndex);

    // Online cinema: the key carries both season and episode, so a series
    // resumes at the exact episode it stopped on
    public TimeSpan? GetSavedVodPosition(string title, int seasonIndex, int episodeIndex)
    {
        if (_vodResumePositions.TryGetValue(VodResumeKey(title, seasonIndex, episodeIndex),
                out var entry) && entry.PositionSeconds >= MinVodResumeSeconds &&
            (entry.DurationSeconds <= 0 ||
             entry.PositionSeconds <= entry.DurationSeconds * VodResumeWatchedFraction))
        {
            return TimeSpan.FromSeconds(entry.PositionSeconds);
        }

        // Pre-season-key entries (or voiceover-only films): the flat key
        if (seasonIndex >= 0 &&
            _vodResumePositions.TryGetValue(VodResumeKey(title, -1, episodeIndex), out entry) &&
            entry.PositionSeconds >= MinVodResumeSeconds &&
            (entry.DurationSeconds <= 0 ||
             entry.PositionSeconds <= entry.DurationSeconds * VodResumeWatchedFraction))
        {
            return TimeSpan.FromSeconds(entry.PositionSeconds);
        }

        return null;
    }

    // Saved season/episode of a series entry — the resume flow starts the
    // exact leaf the playback stopped on. Entries with a season key
    // ("title::sNeM") carry both indexes; flat entries mean a film
    public (int SeasonIndex, int EpisodeIndex)? GetSavedOcPosition(string title)
        => FindLatestOcEntry(title) is { } saved
            ? (saved.SeasonIndex, saved.EpisodeIndex)
            : null;

    // Latest saved position of a series entry with its season/episode slot;
    // null when the title has no season-keyed entry. Flat entries whose
    // fields carry the season/episode pair (legacy keys) are honored too
    public (int SeasonIndex, int EpisodeIndex, TimeSpan? Position)? FindLatestOcEntry(string title)
    {
        var key = _vodResumePositions.Keys
            .Where(k => k.StartsWith(title + "::s", StringComparison.Ordinal))
            .Where(k =>
            {
                var (s, e) = ParseSeasonPart(k);
                return s >= 0 && e >= 0;
            })
            .OrderByDescending(k => _vodResumePositions[k].UpdatedAt)
            .FirstOrDefault();
        if (key != null)
        {
            var (season, episode) = ParseSeasonPart(key);
            var position = GetSavedVodPosition(title, season, episode);
            return (season, episode, position);
        }

        if (_vodResumePositions.TryGetValue(title, out var flat) &&
            flat.OcSeasonIndex >= 0 && flat.EpisodeIndex >= 0 &&
            flat.PositionSeconds >= MinVodResumeSeconds &&
            (flat.DurationSeconds <= 0 ||
             flat.PositionSeconds <= flat.DurationSeconds * VodResumeWatchedFraction))
        {
            return (flat.OcSeasonIndex, flat.EpisodeIndex, TimeSpan.FromSeconds(flat.PositionSeconds));
        }

        return null;
    }

    // "Title::s2e14" → (2, 14)
    internal static (int Season, int Episode) ParseSeasonPart(string key)
    {
        var mark = key.LastIndexOf("::s", StringComparison.Ordinal);
        if (mark < 0)
        {
            return (-1, -1);
        }

        var tail = key[(mark + 3)..];
        var e = tail.IndexOf('e');
        return e > 0 &&
               int.TryParse(tail[..e], out var s) &&
               int.TryParse(tail[(e + 1)..], out var ep)
            ? (s, ep)
            : (-1, -1);
    }

    public async Task<TimeSpan?> OfferVodResumeAsync(string title, int episodeIndex)
    {
        var saved = GetSavedVodPosition(title, -1, episodeIndex);
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

        var isLocalFile = channel.IsLocalFile && !string.IsNullOrWhiteSpace(channel.StreamUrl);
        // Season/episode keys only for series trees — films must keep the
        // flat key (tree indexes of a previous series leak into plain films)
        var isOcSeries = Player.OnlineCinemaPlaylist?.Seasons is { Count: > 0 };
        var key = isLocalFile
            ? LocalFileResumeKey(channel.StreamUrl!)
            : isOcSeries
                ? VodResumeKey(channel.Name, Player.OcSeasonIndex, Player.OcEpisodeIndex)
                : channel.Name;

        var position = Player.VodPositionSeconds;
        if (position < MinVodResumeSeconds)
        {
            _vodResumePositions.Remove(key);
            return;
        }

        var activePlaylist = AppSettings.Playlists.FirstOrDefault(p => p.Id == AppSettings.ActivePlaylistId);
        _vodResumePositions[key] =
            new VodResumePosition
            {
                PositionSeconds = position,
                DurationSeconds = Player.VodDurationSeconds,
                EpisodeIndex = Player.OnlineCinemaPlaylist != null
                    ? Player.OcEpisodeIndex
                    : Player.CurrentVodEpisodeIndex,
                OcSeasonIndex = Player.OnlineCinemaPlaylist != null ? Player.OcSeasonIndex : -1,
                UpdatedAt = DateTime.Now,
                PortalPlaylistId = isLocalFile ? null : activePlaylist?.Id
            };

        PruneVodResumeEntries();

        if ((DateTime.Now - _lastVodResumeSaveRequest).TotalSeconds >= 5)
        {
            _lastVodResumeSaveRequest = DateTime.Now;
            _ = _vodResumeStore.SaveAllAsync(
                new Dictionary<string, VodResumePosition>(_vodResumePositions));
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
        // Immediate silence: the resolve + resume dialog may take seconds, and
        // the previously playing stream must not keep sounding through them.
        // Selecting the same item again keeps its audio untouched
        if (!ReferenceEquals(Player.VodChannel, channel) &&
            !string.Equals(Player.VodChannel?.PageUrl, channel.PageUrl, StringComparison.Ordinal))
        {
            Player.Player?.Pause();
        }

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

            if (flick.Episodes.Count == 0)
            {
                _logger.LogWarning("Портал: «{Name}» не вернул ни одного эпизода.", channel.Name);
                Player.StreamError = string.Format(L.T("Portal_Ne_Otdal_Potok_0"), channel.Name);
                return false;
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

        // Online cinema: resolve a fresh stream from the film page — CDN links
        // carry a date-bound token and expire, so they are never cached
        if (!string.IsNullOrWhiteSpace(channel.PageUrl))
        {
            OnlineCinemaStream? resolved = null;
            Player.IsBuffering = true;
            try
            {
                resolved = await _onlineCinemaResolver.ResolveAsync(channel.PageUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Онлайн-кинотеатр: резолв потока не удался для «{Name}».", channel.Name);
            }
            finally
            {
                Player.IsBuffering = false;
            }

            string? playUrl = resolved?.Url;
            if (string.IsNullOrWhiteSpace(playUrl) && !string.IsNullOrWhiteSpace(channel.StreamUrl))
            {
                _logger.LogInformation(
                    "Онлайн-кинотеатр: резолв не удался — откат на ссылку из каталога для «{Name}».", channel.Name);
                playUrl = channel.StreamUrl;
            }

            if (string.IsNullOrWhiteSpace(playUrl))
            {
                Player.StreamError = L.T("OnlineCinema_Resolv_Fail");
                return false;
            }

            // Default to the best rendition (FFmpeg's own master choice is the
            // lowest one); the picker keeps every rendition plus "Авто".
            // Series: the resolver already played back the default leaf
            // (season 1, episode 1, first voiceover) — cache its URL on the
            // leaf so a later picker switch skips the POST
            var ocPlaylist = resolved?.Playlist;
            if (ocPlaylist?.Seasons is { Count: > 0 } seasons &&
                resolved is { } r)
            {
                seasons[0].Episodes[0].Voiceovers[0].ResolvedUrl = r.Url;
            }

            Dictionary<string, string>? variants = null;
            string? quality = null;
            if (resolved != null)
            {
                variants = resolved.Variants;
                // Plain-HLS series (nextembed): the first episode's variants
                // are built lazily — enrich before picking the rendition
                if (resolved.IsPlainHls &&
                    ocPlaylist?.Seasons is { Count: > 0 } pls &&
                    pls[0].Episodes is { Count: > 0 } eps0 &&
                    eps0[0].Voiceovers is { Count: > 0 } vos0)
                {
                    await _onlineCinemaResolver.EnrichNextEmbedLeafVariantsAsync(vos0[0]);
                    variants = vos0[0].Variants;
                }

                quality = PickDefaultVodQuality(variants);
                if (quality != null)
                {
                    playUrl = variants[quality];
                }
            }

            // Series: start at the saved season/episode instead of the tree's
            // default leaf. The leaf URL/variants resolve through the normal
            // leaf-switch path after the base playback opens (the tree is
            // already attached), the position restores via the leaf switch
            if (interactive && ocPlaylist?.Seasons is { Count: > 0 } savedSeasons &&
                FindLatestOcEntry(channel.Name) is { } saved &&
                saved.SeasonIndex < savedSeasons.Count &&
                saved.EpisodeIndex < savedSeasons[saved.SeasonIndex].Episodes.Count &&
                (saved.SeasonIndex > 0 || saved.EpisodeIndex > 0 || saved.Position != null))
            {
                // Ask first: continue at the saved spot or start over
                var confirmed = OcSeriesResumePromptRequested == null ||
                                await OcSeriesResumePromptRequested(channel.Name,
                                    saved.SeasonIndex, saved.EpisodeIndex, saved.Position ?? TimeSpan.Zero);
                if (confirmed)
                {
                    if (saved.SeasonIndex > 0 || saved.EpisodeIndex > 0)
                    {
                        var leaf = savedSeasons[saved.SeasonIndex]
                            .Episodes[saved.EpisodeIndex].Voiceovers[0];
                        // Pre-resolve so the switch plays instantly; position applies
                        // right after the switch lands (leaf switch resets position)
                        if (leaf.ResolvedUrl == null && !string.IsNullOrEmpty(leaf.Data))
                        {
                            var leafStream = await _onlineCinemaResolver.ResolveLeafAsync(
                                leaf.Origin, leaf.Data, leaf.EmbedUrl);
                            if (leafStream != null)
                            {
                                leaf.ResolvedUrl = leafStream.Url;
                                foreach (var kv in leafStream.Variants)
                                {
                                    leaf.Variants[kv.Key] = kv.Value;
                                }
                            }
                        }

                        await Player.StartPlaybackAsync(channel, leaf.ResolvedUrl ?? playUrl, archiveEntry: null,
                            isVod: true, vodVariants: leaf.Variants.Count > 0 ? leaf.Variants : null,
                            vodQuality: PickDefaultVodQuality(leaf.Variants),
                            resumePosition: saved.Position,
                            onlineCinemaPlaylist: ocPlaylist,
                            onlineCinemaResume: (saved.SeasonIndex, saved.EpisodeIndex));
                        _logger.LogInformation(
                            "Онлайн-кинотеатр: продолжение «{Name}» — сезон {Season}, серия {Episode}.",
                            channel.Name, saved.SeasonIndex, saved.EpisodeIndex);
                        return true;
                    }

                    // Stopped on the tree's default leaf (s1e1) — the position
                    // alone still needs restoring
                    if (saved.Position is { } defaultLeafPosition)
                    {
                        await Player.StartPlaybackAsync(channel, playUrl, archiveEntry: null,
                            isVod: true, vodVariants: variants, vodQuality: quality,
                            resumePosition: defaultLeafPosition, onlineCinemaPlaylist: ocPlaylist);
                        return true;
                    }
                }

                // Declined — fall through to a fresh start
            }

            var cinemaResume = interactive
                ? await OfferVodResumeAsync(channel.Name, -1)
                : null;
            await Player.StartPlaybackAsync(channel, playUrl, archiveEntry: null,
                isVod: true, vodVariants: variants, vodQuality: quality, resumePosition: cinemaResume,
                onlineCinemaPlaylist: ocPlaylist);
            return true;
        }

        // m3u VOD catalog: same resume + pause treatment as portal catalog items
        if (_isVodSource && !string.IsNullOrWhiteSpace(channel.StreamUrl))
        {
            var resume = interactive
                ? await OfferVodResumeAsync(channel.Name, -1)
                : null;
            await Player.StartPlaybackAsync(channel, channel.StreamUrl!, archiveEntry: null,
                isVod: true, resumePosition: resume);
            return true;
        }

        await Player.PlayLiveAsync(channel);
        return !string.IsNullOrWhiteSpace(channel.StreamUrl);
    }

    // PreferredQuality from settings wins when available, otherwise the maximum
    private string? PickDefaultVodQuality(Dictionary<string, string> variants)
    {
        var preferred = AppSettings.PreferredQuality;
        if (preferred > 0)
        {
            var key = preferred + "p";
            if (variants.ContainsKey(key))
            {
                return key;
            }
        }

        return variants.Keys
            .Where(k => k.EndsWith('p') && int.TryParse(k[..^1], out _))
            .OrderByDescending(k => int.Parse(k[..^1]))
            .FirstOrDefault();
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
