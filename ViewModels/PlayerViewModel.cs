using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;
using Windows.Media.Playback;

namespace IptvPlayer.ViewModels;


public partial class PlayerViewModel : ObservableObject
{
    private readonly IStreamService _streamService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<PlayerViewModel> _logger;

    private string _streamId = string.Empty;
    private string? _lastStreamUrl;

    public string StreamId
    {
        get => _streamId;
        set => SetProperty(ref _streamId, value);
    }

    private string _currentPosition = "00:00:00";

    public string CurrentPosition
    {
        get => _currentPosition;
        set => SetProperty(ref _currentPosition, value);
    }

    private MediaPlayer? _player;

    public MediaPlayer? Player
    {
        get => _player;
        set => SetProperty(ref _player, value);
    }

    private bool _isBuffering;

    public bool IsBuffering
    {
        get => _isBuffering;
        set => SetProperty(ref _isBuffering, value);
    }

    private string? _streamError;

    public string? StreamError
    {
        get => _streamError;
        set => SetProperty(ref _streamError, value);
    }

    public int? CurrentPlayerChannelId { get; private set; }

    public bool IsArchivePlaying { get; private set; }


    public bool IsVodPlaying { get; private set; }

    private ChannelViewModel? _vodChannel;
    private Dictionary<string, string> _vodVariantUrls = new();


    public IReadOnlyList<string> VodQualities { get; private set; } = Array.Empty<string>();


    public string? CurrentVodQuality { get; private set; }

    private List<PortalEpisode> _vodEpisodes = new();


    public IReadOnlyList<PortalEpisode> VodEpisodes { get; private set; } = Array.Empty<PortalEpisode>();


    public int CurrentVodEpisodeIndex { get; private set; } = -1;


    public ChannelViewModel? VodChannel => _vodChannel;


    // Online cinema: season/episode/voiceover tree for the picker combos
    public Services.OnlineCinemaPlaylist? OnlineCinemaPlaylist { get; private set; }

    public int OcSeasonIndex { get; private set; } = -1;

    public int OcEpisodeIndex { get; private set; } = -1;

    public int OcVoiceoverIndex { get; private set; } = -1;

    private Services.IOnlineCinemaStreamResolver _onlineCinemaResolver;

    // Switches season/episode/voiceover (-1 = keep the current one) and plays
    // the leaf: lazy api resolution, quality = maximum rendition, position
    // preserved when only the voiceover changes
    public async Task PlayOnlineCinemaLeafAsync(int seasonIndex, int episodeIndex, int voiceoverIndex)
    {
        var playlist = OnlineCinemaPlaylist;
        if (playlist == null || _vodChannel == null || !IsVodPlaying)
        {
            return;
        }

        seasonIndex = seasonIndex < 0 ? OcSeasonIndex : seasonIndex;
        episodeIndex = episodeIndex < 0 ? OcEpisodeIndex : episodeIndex;
        voiceoverIndex = voiceoverIndex < 0 ? OcVoiceoverIndex : voiceoverIndex;

        Services.OnlineCinemaLeaf? leaf;
        if (playlist.Seasons is { Count: > 0 } seasons)
        {
            if (seasonIndex < 0 || seasonIndex >= seasons.Count ||
                episodeIndex < 0 || episodeIndex >= seasons[seasonIndex].Episodes.Count ||
                voiceoverIndex < 0 || voiceoverIndex >= seasons[seasonIndex].Episodes[episodeIndex].Voiceovers.Count)
            {
                return;
            }

            leaf = seasons[seasonIndex].Episodes[episodeIndex].Voiceovers[voiceoverIndex];
        }
        else if (playlist.Voiceovers is { Count: > 0 } voiceovers &&
                 voiceoverIndex >= 0 && voiceoverIndex < voiceovers.Count)
        {
            leaf = voiceovers[voiceoverIndex];
        }
        else
        {
            return;
        }

        var sameEpisode = playlist.Seasons != null &&
                          seasonIndex == OcSeasonIndex && episodeIndex == OcEpisodeIndex;
        var resume = sameEpisode && Player is { } current ? current.Position : TimeSpan.Zero;
        OcSeasonIndex = seasonIndex;
        OcEpisodeIndex = episodeIndex;
        OcVoiceoverIndex = voiceoverIndex;

        IsBuffering = true;
        Services.OnlineCinemaStream? resolved;
        try
        {
            resolved = leaf.ResolvedUrl != null
                ? new Services.OnlineCinemaStream { Url = leaf.ResolvedUrl }
                : await _onlineCinemaResolver.ResolveLeafAsync(leaf.Origin, leaf.Data, leaf.EmbedUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Онлайн-кинотеатр: резолв листа не удался.");
            StreamError = Services.L.T("OnlineCinema_Resolv_Fail");
            return;
        }
        finally
        {
            IsBuffering = false;
        }

        if (resolved == null)
        {
            StreamError = Services.L.T("OnlineCinema_Resolv_Fail");
            return;
        }

        if (leaf.ResolvedUrl == null)
        {
            leaf.ResolvedUrl = resolved.Url;
        }

        var quality = resolved.Variants.Keys
            .Where(k => k.EndsWith('p') && int.TryParse(k[..^1], out _))
            .OrderByDescending(k => int.Parse(k[..^1]))
            .FirstOrDefault();
        var url = quality != null ? resolved.Variants[quality] : resolved.Url;
        await StartPlaybackAsync(_vodChannel, url, archiveEntry: null, isVod: true,
            vodVariants: resolved.Variants.Count > 0 ? resolved.Variants : null,
            vodQuality: quality, resumePosition: resume > TimeSpan.Zero ? resume : null,
            onlineCinemaPlaylist: playlist);
    }


    public async Task PlayVodEpisodeAsync(int index)
    {
        if (!IsVodPlaying || _vodChannel == null ||
            index < 0 || index >= _vodEpisodes.Count)
        {
            return;
        }

        var episode = _vodEpisodes[index];
        _logger.LogInformation("VOD: серия {Current} → {Next} («{Title}»).",
            CurrentVodEpisodeIndex + 1, index + 1, episode.Title);
        // Carry the current quality over; if the new episode lacks it, the
        // maximum rendition beats the master (FFmpeg's own default is lowest)
        var quality = CurrentVodQuality;
        if (quality == null || (episode.Variants.Count > 0 && !episode.Variants.ContainsKey(quality)))
        {
            quality = episode.Variants.Keys
                .Where(k => k.EndsWith('p') && int.TryParse(k[..^1], out _))
                .OrderByDescending(k => int.Parse(k[..^1]))
                .FirstOrDefault();
        }

        await StartPlaybackAsync(_vodChannel, episode.StreamUrl, archiveEntry: null, isVod: true,
            vodVariants: episode.Variants.Count > 0 ? episode.Variants : null,
            vodQuality: quality,
            vodEpisodes: _vodEpisodes, vodEpisodeIndex: index);
    }


    public event EventHandler? VodStateChanged;

    private IReadOnlyList<string> _audioTrackLabels = Array.Empty<string>();


    // Labels like "Track 1 (rus)" for the audio track picker on the player overlay
    public IReadOnlyList<string> AudioTrackLabels
    {
        get => _audioTrackLabels;
        private set => SetProperty(ref _audioTrackLabels, value);
    }

    private int _currentAudioTrackIndex;

    public int CurrentAudioTrackIndex
    {
        get => _currentAudioTrackIndex;
        private set => SetProperty(ref _currentAudioTrackIndex, value);
    }


    public void SelectAudioTrack(int index)
    {
        if (Player is null || !_streamService.TrySelectAudioTrack(Player, index))
        {
            return;
        }

        CurrentAudioTrackIndex = index;
        _logger.LogInformation(
            "Аудиодорожка: выбрана вручную дорожка {Index} из {Count}.", index + 1, AudioTrackLabels.Count);
    }


    private void RefreshAudioTracks()
    {
        if (Player is null)
        {
            AudioTrackLabels = Array.Empty<string>();
            CurrentAudioTrackIndex = -1;
            return;
        }

        var tracks = _streamService.GetAudioTracks(Player);

        // Unlabeled multi-audio is usually one audio duplicated per HLS rendition (OttPlayer shows it
        // as a single track) — a picker over identical tracks is meaningless, so hide it
        var hasLanguage = tracks.Any(t => !string.IsNullOrWhiteSpace(t.Language));
        if (tracks.Count > 1 && !hasLanguage)
        {
            AudioTrackLabels = Array.Empty<string>();
            CurrentAudioTrackIndex = -1;
            return;
        }

        var labels = new List<string>(tracks.Count);
        foreach (var track in tracks)
        {
            var baseLabel = string.Format(L.T("Audio_Dorozhka_N"), track.Index + 1);
            labels.Add(string.IsNullOrWhiteSpace(track.Language) ? baseLabel : $"{baseLabel} ({track.Language})");
        }

        AudioTrackLabels = labels;
        CurrentAudioTrackIndex = _streamService.GetSelectedAudioTrackIndex(Player);
    }

    private double _vodPositionSeconds;
    private double _vodDurationSeconds;


    public bool IsVodSeeking { get; set; }

    public double VodPositionSeconds
    {
        get => _vodPositionSeconds;
        private set => SetProperty(ref _vodPositionSeconds, value);
    }

    public double VodDurationSeconds
    {
        get => _vodDurationSeconds;
        private set => SetProperty(ref _vodDurationSeconds, value);
    }

    public string VodPositionText { get; private set; } = "00:00";
    public string VodDurationText { get; private set; } = "00:00";


    public void RefreshVodPosition()
    {
        if (!IsVodPlaying || Player?.PlaybackSession == null)
        {
            return;
        }

        try
        {
            VodDurationSeconds = Player.PlaybackSession.NaturalDuration.TotalSeconds;
            if (!IsVodSeeking)
            {
                VodPositionSeconds = Player.PlaybackSession.Position.TotalSeconds;
            }

            VodPositionText = FormatArchiveTime(VodPositionSeconds);
            VodDurationText = FormatArchiveTime(VodDurationSeconds);
            OnPropertyChanged(nameof(VodPositionText));
            OnPropertyChanged(nameof(VodDurationText));
        }
        catch (Exception ex)
        {

            _logger.LogDebug(ex, "VOD: позиция недоступна.");
        }
    }


    public void SeekVod(double positionSeconds)
    {
        if (!IsVodPlaying || Player?.PlaybackSession == null)
        {
            return;
        }

        var duration = Player.PlaybackSession.NaturalDuration.TotalSeconds;
        if (duration > 0)
        {
            positionSeconds = Math.Clamp(positionSeconds, 0.0, Math.Max(0.0, duration - 1));
        }

        try
        {
            Player.PlaybackSession.Position = TimeSpan.FromSeconds(positionSeconds);
            VodPositionSeconds = positionSeconds;
            _logger.LogInformation("VOD: перемотка на {Position}.", TimeSpan.FromSeconds(positionSeconds));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VOD: перемотка на {Position} не удалась.", positionSeconds);
        }
    }

    public EPGEntry? ArchiveEntry { get; private set; }

    private ChannelViewModel? _archiveChannel;
    private DateTime _archivePlayStartWallUtc;
    private DateTime _archiveStartPosition;
    private TimeSpan _archivePausedTotal;
    private DateTime? _archivePausedAtUtc;
    private double _archivePositionSeconds;
    private double _archiveDurationSeconds;


    public bool IsArchiveSeeking { get; set; }


    public double ArchivePositionSeconds
    {
        get => _archivePositionSeconds;
        private set => SetProperty(ref _archivePositionSeconds, value);
    }


    public double ArchiveDurationSeconds
    {
        get => _archiveDurationSeconds;
        private set => SetProperty(ref _archiveDurationSeconds, value);
    }


    public string ArchivePositionText { get; private set; } = "00:00";


    public string ArchiveDurationText { get; private set; } = "00:00";


    public void RefreshArchivePosition()
    {
        if (!IsArchivePlaying || ArchiveEntry == null)
        {
            return;
        }

        var wallElapsed = DateTime.UtcNow - _archivePlayStartWallUtc - _archivePausedTotal;
        if (_archivePausedAtUtc is { } pausedAt)
        {
            wallElapsed -= DateTime.UtcNow - pausedAt;
        }

        var position = (_archiveStartPosition - ArchiveEntry.StartTime).TotalSeconds + wallElapsed.TotalSeconds;
        var total = (ArchiveEntry.EndTime - ArchiveEntry.StartTime).TotalSeconds;
        var liveEdge = (DateTime.Now - ArchiveEntry.StartTime).TotalSeconds;

        ArchivePositionSeconds = Math.Clamp(position, 0.0, Math.Min(total, Math.Max(0.0, liveEdge)));
        ArchiveDurationSeconds = Math.Max(1.0, total);
        ArchivePositionText = FormatArchiveTime(ArchivePositionSeconds);
        ArchiveDurationText = FormatArchiveTime(ArchiveDurationSeconds);

        OnPropertyChanged(nameof(ArchivePositionText));
        OnPropertyChanged(nameof(ArchiveDurationText));
    }


    public async Task SeekArchiveAsync(double positionSeconds)
    {
        if (!IsArchivePlaying || ArchiveEntry == null ||
            _archiveChannel == null || string.IsNullOrWhiteSpace(_archiveChannel.StreamUrl))
        {
            return;
        }

        var start = ArchiveEntry.StartTime + TimeSpan.FromSeconds(Math.Max(0, positionSeconds));

        var liveEdge = DateTime.Now.AddSeconds(-5);
        if (start > liveEdge)
        {
            start = liveEdge;
        }

        var url = ArchiveUrlBuilder.BuildUrl(_archiveChannel.StreamUrl, start);
        _logger.LogInformation(
            "Перемотка архива: передача {Program} [{Start:HH:mm:ss}-{End:HH:mm:ss}], позиция {Position:F0} c, новая точка старта {SeekStart:HH:mm:ss}.",
            ArchiveEntry.ProgramName, ArchiveEntry.StartTime, ArchiveEntry.EndTime, positionSeconds, start);
        await StartPlaybackAsync(_archiveChannel, url, ArchiveEntry, archivePlayStart: start);
    }


    internal static string FormatArchiveTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss")
            : t.ToString(@"mm\:ss");
    }


    public double? LastUserVolume { get; set; }


    public event EventHandler? PlayerChanged;


    public event EventHandler? ArchiveStateChanged;

    public PlayerViewModel(IStreamService streamService, ISettingsService settingsService,
        Services.IOnlineCinemaStreamResolver onlineCinemaResolver, ILogger<PlayerViewModel> logger)
    {
        _streamService = streamService;
        _settingsService = settingsService;
        _onlineCinemaResolver = onlineCinemaResolver;
        _logger = logger;
    }


    public async Task PlayLiveAsync(ChannelViewModel channel)
    {
        if (string.IsNullOrWhiteSpace(channel.StreamUrl))
        {
            StreamError = L.T("U_Kanala_Ne_Ukazan_URL_Potoka");
            return;
        }

        await StartPlaybackAsync(channel, channel.StreamUrl, archiveEntry: null);
    }


    public async Task StartPlaybackAsync(ChannelViewModel channel, string streamUrl, EPGEntry? archiveEntry, DateTime? archivePlayStart = null, bool isVod = false, Dictionary<string, string>? vodVariants = null, string? vodQuality = null, TimeSpan? resumePosition = null, IReadOnlyList<PortalEpisode>? vodEpisodes = null, int vodEpisodeIndex = -1, Services.OnlineCinemaPlaylist? onlineCinemaPlaylist = null)
    {

        Stop();
        var generation = _playbackGeneration;
        _zapCts?.Cancel();
        // No Dispose: token may still be registered in an in-flight CreatePlayerAsync
        _zapCts = new System.Threading.CancellationTokenSource();
        var zapCts = _zapCts;

        StreamError = null;
        IsBuffering = true;
        _lastStreamUrl = streamUrl;

            var startWait = System.Diagnostics.Stopwatch.StartNew();
            var streamSettings = await _settingsService.LoadAsync();
            var streamConfig = new PlaybackConfig(
                streamSettings.DecoderMode,
                streamSettings.AudioNormalization,
                streamSettings.AudioVolumeBoost,
                streamSettings.ReadAheadSeconds,
                streamSettings.VodReadAheadSeconds,
                streamSettings.DiagnosticStreamProxy,
                streamSettings.VideoUpscaler,
                streamSettings.FrameServerRender,
                streamSettings.PreferredAudioLanguage);
            try
            {
                var player = await _streamService.CreatePlayerAsync(streamUrl, streamConfig, isVod, zapCts.Token);
            if (generation != _playbackGeneration)
            {


                _logger.LogInformation(
                    "ЗАПУСК-ТАЙМИНГ: запуск «{Channel}» устарел (поколение {Generation}/{Current}) — освобождаем без воспроизведения.",
                    channel.Name, generation, _playbackGeneration);
                try
                {
                    player.MediaFailed -= OnMediaFailed;
                    _streamService.ReleasePlayer(player);
                }
                catch
                {

                }
                return;
            }
            _logger.LogInformation(
                "ЗАПУСК-ТАЙМИНГ: CreatePlayerAsync «{Channel}» занял {Ms:F0} мс.",
                channel.Name, startWait.Elapsed.TotalMilliseconds);
            player.MediaFailed += OnMediaFailed;

            if (IsMuted)
            {
                player.Volume = 0;
            }
            else if (LastUserVolume.HasValue)
            {
                player.Volume = LastUserVolume.Value;
            }

            Player = player;
            RefreshAudioTracks();
            EnsureDisplayRequest();
            CurrentPlayerChannelId = channel.Id;
            IsArchivePlaying = archiveEntry != null;
            IsVodPlaying = isVod && archiveEntry == null;
            OnlineCinemaPlaylist = isVod ? onlineCinemaPlaylist : null;
            if (OnlineCinemaPlaylist != null)
            {
                // New film resets the tree position; a leaf switch keeps it
                // (indexes are already set by PlayOnlineCinemaLeafAsync)
                if (!ReferenceEquals(_vodChannel, channel))
                {
                    OcSeasonIndex = 0;
                    OcEpisodeIndex = 0;
                    OcVoiceoverIndex = 0;
                }
                else
                {
                    OcSeasonIndex = Math.Max(0, OcSeasonIndex);
                    OcEpisodeIndex = Math.Max(0, OcEpisodeIndex);
                    OcVoiceoverIndex = Math.Max(0, OcVoiceoverIndex);
                }
            }

            if (IsVodPlaying)
            {
                _vodChannel = channel;
                if (vodVariants is { Count: > 0 })
                {

                    _vodVariantUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in vodVariants)
                    {
                        _vodVariantUrls[VodQualityLabel(kv.Key)] = kv.Value;
                    }
                }

                if (vodEpisodes is { Count: > 0 })
                {
                    _vodEpisodes = vodEpisodes.ToList();
                    VodEpisodes = _vodEpisodes;
                    CurrentVodEpisodeIndex = vodEpisodeIndex;
                }

                // Episodes often come as a single master.m3u8 without a portal variants dict —
                // offer the renditions parsed from the master playlist, with the master itself as "Авто"
                var masterApplied = false;
                if (_vodVariantUrls.Count == 0 &&
                    _streamService.TryGetVodMasterVariants(player, out var masterVariants))
                {
                    masterVariants["Авто"] = streamUrl;
                    SetVodVariants(masterVariants);
                    masterApplied = true;
                }

                VodQualities = OrderVodQualities(_vodVariantUrls.Keys);
                if (vodQuality is { } requested && VodQualities.Contains(requested))
                {
                    CurrentVodQuality = requested;
                }
                else if (vodQuality is null && masterApplied && _vodVariantUrls.ContainsKey("Авто"))
                {
                    CurrentVodQuality = "Авто";
                }
                else
                {
                    CurrentVodQuality = null;
                }

                if (resumePosition is { } resume && resume > TimeSpan.Zero)
                {
                    try
                    {
                        player.Position = resume;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "VOD: не удалось перемотать на прежнюю позицию {Position}.", resume);
                    }
                }
            }
            else
            {
                _vodChannel = null;
                _vodVariantUrls = new Dictionary<string, string>();
                VodQualities = Array.Empty<string>();
                CurrentVodQuality = null;
                _vodEpisodes = new List<PortalEpisode>();
                VodEpisodes = Array.Empty<PortalEpisode>();
                CurrentVodEpisodeIndex = -1;
            }

            ArchiveEntry = archiveEntry;
            StreamId = streamUrl;
            channel.IsPlaying = true;

            if (archiveEntry != null)
            {
                _archiveChannel = channel;
                _archivePlayStartWallUtc = DateTime.UtcNow;
                _archiveStartPosition = archivePlayStart ?? archiveEntry.StartTime;
                _archivePausedTotal = TimeSpan.Zero;
                _archivePausedAtUtc = null;
                ArchivePositionSeconds = Math.Max(0,
                    (_archiveStartPosition - archiveEntry.StartTime).TotalSeconds);
                ArchiveDurationSeconds = Math.Max(1,
                    (archiveEntry.EndTime - archiveEntry.StartTime).TotalSeconds);
            }

            PlayerChanged?.Invoke(this, EventArgs.Empty);
            ArchiveStateChanged?.Invoke(this, EventArgs.Empty);
            VodStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (generation != _playbackGeneration || zapCts.IsCancellationRequested)
        {
            // Stale or cancelled zap — not an error
            _logger.LogInformation("StartPlaybackAsync: запуск «{Channel}» отменён.", channel.Name);
            channel.IsPlaying = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartPlaybackAsync: канал {ChannelId}, url {Url}.", channel.Id, streamUrl);
            StreamError = string.Format(L.T("Ne_Udalos_Vosproizvesti_Potok_0"), ex.Message, ex.Message);
            channel.IsPlaying = false;
        }
        finally
        {
            IsBuffering = false;
        }
    }

    private bool _displayRequested;
    private readonly Windows.System.Display.DisplayRequest _displayRequest = new();

    private int _playbackGeneration;

    // Cancelled on Stop()/new playback so a hung stream open can't linger
    private System.Threading.CancellationTokenSource? _zapCts;

    private void EnsureDisplayRequest()
    {
        if (_displayRequested)
        {
            return;
        }
        try
        {
            _displayRequest.RequestActive();
            _displayRequested = true;
            _logger.LogDebug("DisplayRequest: активен (экран не гаснет)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisplayRequest: RequestActive не сработал");
        }
    }

    private void ReleaseDisplayRequest()
    {
        if (!_displayRequested)
        {
            return;
        }
        try
        {
            _displayRequest.RequestRelease();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisplayRequest: RequestRelease не сработал");
        }
        _displayRequested = false;
    }


    public void Stop()
    {
        _playbackGeneration++;
        try { _zapCts?.Cancel(); } catch (ObjectDisposedException) { }
        ReleaseDisplayRequest();
        if (Player == null)
        {
            return;
        }

        var player = Player;
        Player = null;
        CurrentPlayerChannelId = null;
        IsArchivePlaying = false;
        IsVodPlaying = false;
        _vodChannel = null;
        _vodVariantUrls = new Dictionary<string, string>();
        VodQualities = Array.Empty<string>();
        CurrentVodQuality = null;
        _vodEpisodes = new List<PortalEpisode>();
        VodEpisodes = Array.Empty<PortalEpisode>();
        CurrentVodEpisodeIndex = -1;
        ArchiveEntry = null;
        _archiveChannel = null;
        _archivePausedAtUtc = null;

        PlayerChanged?.Invoke(this, EventArgs.Empty);
        ArchiveStateChanged?.Invoke(this, EventArgs.Empty);
        VodStateChanged?.Invoke(this, EventArgs.Empty);

        player.MediaFailed -= OnMediaFailed;
        _streamService.ReleasePlayer(player);
    }


    public bool ToggleArchivePause(ChannelViewModel? selectedChannel)
    {
        if (Player == null || selectedChannel == null || CurrentPlayerChannelId != selectedChannel.Id)
        {
            return false;
        }

        if (selectedChannel.IsPlaying)
        {


            _archivePausedAtUtc = DateTime.UtcNow;
            Player.Pause();
            selectedChannel.IsPlaying = false;
        }
        else
        {
            if (_archivePausedAtUtc is { } pausedAt)
            {
                _archivePausedTotal += DateTime.UtcNow - pausedAt;
                _archivePausedAtUtc = null;
            }
            Player.Play();
            selectedChannel.IsPlaying = true;
        }

        ArchiveStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }


    public void ResetArchiveState()
    {
        IsArchivePlaying = false;
        ArchiveEntry = null;
        ArchiveStateChanged?.Invoke(this, EventArgs.Empty);
    }


    public void SetVodVariants(Dictionary<string, string> variants)
    {
        if (!IsVodPlaying || variants.Count == 0)
        {
            return;
        }

        foreach (var pair in variants)
        {
            _vodVariantUrls[VodQualityLabel(pair.Key)] = pair.Value;
        }

        VodQualities = OrderVodQualities(_vodVariantUrls.Keys);
        VodStateChanged?.Invoke(this, EventArgs.Empty);
    }


    public async Task SwitchVodQualityAsync(string quality)
    {
        if (!IsVodPlaying || _vodChannel == null || !_vodVariantUrls.TryGetValue(quality, out var url))
        {
            return;
        }

        var resume = Player?.Position ?? TimeSpan.Zero;
        _logger.LogInformation("VOD: качество {Current} → {Next} (позиция {Position}).",
            CurrentVodQuality ?? "?", quality, resume);
        await StartPlaybackAsync(_vodChannel, url, archiveEntry: null, isVod: true,
            vodVariants: _vodVariantUrls, vodQuality: quality, resumePosition: resume);
    }


    private static string VodQualityLabel(string key)
    {
        if (key.Equals("auto", StringComparison.OrdinalIgnoreCase) || key == "Авто")
        {
            return "Авто";
        }

        if (key.EndsWith('p') && int.TryParse(key[..^1], out _))
        {
            return key;
        }

        return key + "p";
    }


    private static IReadOnlyList<string> OrderVodQualities(IEnumerable<string> labels)
    {
        static string? NumericKey(string label) =>
            label.EndsWith('p') && int.TryParse(label[..^1], out var n) ? n.ToString() : null;

        var list = labels.Distinct().ToList();
        var auto = list.Where(l => l == "Авто").ToList();
        var numeric = list.Where(l => NumericKey(l) != null && l != "Авто")
            .OrderByDescending(l => int.Parse(l[..^1]))
            .ToList();
        var other = list.Where(l => l != "Авто" && NumericKey(l) == null)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return auto.Concat(numeric).Concat(other).ToList();
    }

    private bool _isMuted;
    private double? _volumeBeforeMute;


    public bool IsMuted => _isMuted;


    public void ToggleMute()
    {
        if (_isMuted)
        {
            _isMuted = false;
            var restore = _volumeBeforeMute ?? LastUserVolume ?? 1.0;
            if (restore <= 0.001)
            {

                restore = 1.0;
            }
            _volumeBeforeMute = null;
            if (Player != null)
            {
                Player.Volume = restore;
            }
        }
        else
        {
            _volumeBeforeMute = LastUserVolume ?? Player?.Volume ?? 1.0;
            _isMuted = true;
            if (Player != null)
            {
                Player.Volume = 0;
            }
        }

        OnPropertyChanged(nameof(IsMuted));
    }


    public void ClearMute()
    {
        if (!_isMuted)
        {
            return;
        }

        _isMuted = false;
        _volumeBeforeMute = null;
        OnPropertyChanged(nameof(IsMuted));
    }


    private string _videoUpscaler = VideoUpscaler.Off;

    public string VideoUpscalerMode
    {
        get => _videoUpscaler;
        set => SetProperty(ref _videoUpscaler, value);
    }


    public async Task SetVideoUpscalerAsync(string mode)
    {
        var normalized = VideoUpscaler.Normalize(mode);

        if (VodChannel is { IsLocalFile: true } localChannel &&
            !string.IsNullOrWhiteSpace(localChannel.StreamUrl))
        {
            VideoUpscalerMode = normalized;
            try
            {
                var localSettings = await _settingsService.LoadAsync();
                localSettings.VideoUpscaler = normalized;
                await _settingsService.SaveAsync(localSettings);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось сохранить пресет улучшения картинки.");
            }

            var resume = VodPositionSeconds;
            if (resume > 0)
            {
                await StartPlaybackAsync(localChannel, localChannel.StreamUrl!, archiveEntry: null,
                    isVod: true, resumePosition: TimeSpan.FromSeconds(resume));
            }
            else
            {
                await StartPlaybackAsync(localChannel, localChannel.StreamUrl!, archiveEntry: null, isVod: true);
            }

            _logger.LogInformation(
                "Пресет улучшения картинки {Mode} применён рестартом локального файла (живая смена фильтров его стопорит).",
                normalized);
            return;
        }

        if (normalized == _videoUpscaler && normalized == VideoUpscaler.Off)
        {

            _streamService.ApplyVideoFilters(Player, normalized);
            return;
        }

        VideoUpscalerMode = normalized;
        _streamService.ApplyVideoFilters(Player, normalized);

        try
        {
            var settings = await _settingsService.LoadAsync();
            settings.VideoUpscaler = normalized;
            await _settingsService.SaveAsync(settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось сохранить пресет улучшения картинки.");
        }
    }

    private async void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        _logger.LogError(
            "MediaPlayer.MediaFailed: Status={Status}, Code=0x{Code:x}{Message}",
            args.Error, args.ExtendedErrorCode,
            string.IsNullOrEmpty(args.ErrorMessage) ? string.Empty : $", {args.ErrorMessage}");

        // Stale handler from a released player must not clobber the new playback state
        if (!ReferenceEquals(sender, Player))
        {
            return;
        }

        try
        {
        var errorMsg = args.ErrorMessage ?? args.Error.ToString();
        var diagnostic = await _streamService.DiagnoseStreamUrl(_lastStreamUrl);

        StreamError = $"{L.T("Oshibka_Vosproizvedeniya")}{errorMsg}\n\n{string.Format(L.T("Diagnostika_Prefiks_0"), diagnostic)}";
        IsBuffering = false;
        ResetArchiveState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnMediaFailed: не удалось показать диагностику ошибки потока.");
        }
    }
}
