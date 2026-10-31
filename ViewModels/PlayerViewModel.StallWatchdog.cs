using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Windows.Media.Playback;

namespace IptvPlayer.ViewModels;

// VOD/live stall watchdog: a CDN that accepts TCP but stops sending data
// freezes the player with no MediaFailed — reopen the stream (VOD: from the
// last position, live: at the live edge)
public partial class PlayerViewModel
{
    private const int StallTickSeconds = 5;
    private const int StallThresholdSeconds = 20;
    private const int StallMaxRestarts = 3;

    // 60 s of continuous progress re-arms the restart budget
    private const int StallStableTicksToReset = 12;

    private Windows.System.DispatcherQueueTimer? _stallTimer;
    private MediaPlayer? _stallPlayer;
    private TimeSpan _stallLastPosition;
    private DateTime? _stallSinceUtc;
    private int _stallStableTicks;
    private int _stallRestarts;
    private bool _stallGiveUp;
    private bool _stallAutoRestart;
    private int _stallTicksSinceHeartbeat;

    private void EnsureStallWatchdog()
    {
        if (_stallTimer != null)
        {
            return;
        }

        try
        {
            var timer = Windows.System.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(StallTickSeconds);
            timer.Tick += (_, _) => CheckStall();
            timer.Start();
            _stallTimer = timer;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Сторож зависшего потока не запущен (нет DispatcherQueue на потоке).");
        }
    }

    private void CheckStall()
    {
        // Archive is excluded: a restart would lose the seek point (the archive
        // URL carries the start time and StartPlaybackAsync cannot resume inside it)
        var isVod = IsVodPlaying;
        var channel = isVod ? _vodChannel : (IsArchivePlaying ? null : _liveChannel);
        if (channel == null || _lastStreamUrl == null || IsBuffering)
        {
            ResetStallTracking();
            return;
        }

        var player = Player;
        var session = player?.PlaybackSession;
        if (player == null || session == null)
        {
            ResetStallTracking();
            return;
        }

        // Fresh player (manual start or watchdog restart) — restart the clock
        if (!ReferenceEquals(player, _stallPlayer))
        {
            _stallPlayer = player;
            _stallLastPosition = TimeSpan.Zero;
            _stallSinceUtc = DateTime.UtcNow;
            _stallStableTicks = 0;
            return;
        }

        var state = session.PlaybackState;
        if (state is MediaPlaybackState.Paused or MediaPlaybackState.None)
        {
            _stallSinceUtc = null;
            return;
        }

        var position = session.Position;
        var duration = session.NaturalDuration;

        // Minute heartbeat: a video freeze with live audio keeps the clock
        // running — the log trail is the only way to see it after the fact
        if (++_stallTicksSinceHeartbeat >= 12)
        {
            _stallTicksSinceHeartbeat = 0;
            _logger.LogInformation(
                "Пульс: «{Channel}» ({Mode}) состояние={State} позиция={Position:mm\\:ss} рестартов={Restarts}.",
                channel.Name, isVod ? "VOD" : "эфир", state, position, _stallRestarts);

            // Diagnostic proxy on → per-minute average stream speed for freeze correlation
            if (_streamService.ProxyMeasuredBitrate is { } bps && bps > 0)
            {
                var min = _streamService.ProxyMinBitrateLastMinute;
                var minText = min is { } m ? $" (мин 10-с окно {m / 1_000_000.0:F1})" : string.Empty;
                _logger.LogInformation(
                    "Пульс: скорость потока (прокси) {Mbps:F1} Мбит/с{Min}.",
                    bps / 1_000_000.0, minText);
            }
        }

        // Media ended — the position legitimately stops near the duration.
        // VOD only: a live stream sitting near its reported duration is normal
        if (isVod && duration > TimeSpan.Zero && position >= duration - TimeSpan.FromSeconds(5))
        {
            _stallSinceUtc = null;
            return;
        }

        if (Math.Abs((position - _stallLastPosition).TotalMilliseconds) >= 500)
        {
            var advanced = position > _stallLastPosition;
            _stallLastPosition = position;
            _stallSinceUtc = null;
            if (advanced && _stallStableTicks < StallStableTicksToReset &&
                ++_stallStableTicks == StallStableTicksToReset)
            {
                _stallRestarts = 0;
                _stallGiveUp = false;
            }

            return;
        }

        _stallSinceUtc ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _stallSinceUtc.Value < TimeSpan.FromSeconds(StallThresholdSeconds))
        {
            return;
        }

        if (_stallGiveUp)
        {
            return;
        }

        if (_stallRestarts >= StallMaxRestarts)
        {
            _stallGiveUp = true;
            _logger.LogError(
                "Сторож: {Max} перезапусков «{Channel}» ({Mode}) не помогли — автовосстановление остановлено.",
                StallMaxRestarts, channel.Name, isVod ? "VOD" : "эфир");
            StreamError = Services.L.T("Stall_Watchdog_Give_Up");
            return;
        }

        _stallSinceUtc = null;
        _ = StallRestartAsync(isVod, channel, position);
    }

    private void ResetStallTracking()
    {
        _stallPlayer = null;
        _stallLastPosition = TimeSpan.Zero;
        _stallSinceUtc = null;
        _stallStableTicks = 0;
        _stallTicksSinceHeartbeat = 0;
    }

    private async Task StallRestartAsync(bool isVod, ChannelViewModel channel, TimeSpan resume)
    {
        _stallRestarts++;
        _logger.LogWarning(
            "Сторож: поток «{Channel}» ({Mode}) не продвигается {Seconds} с — перезапуск {Attempt}/{Max}{Position}.",
            channel.Name, isVod ? "VOD" : "эфир", StallThresholdSeconds,
            _stallRestarts, StallMaxRestarts,
            isVod ? $" с позиции {resume}" : " с живого края");

        if (isVod)
        {
            await ReopenVodAsync(resume, "зависание потока");
            return;
        }

        // StartPlaybackAsync must not consume the retry budget on this call
        _stallAutoRestart = true;
        try
        {
            await StartPlaybackAsync(channel, _lastStreamUrl!, archiveEntry: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Сторож: перезапуск «{Channel}» не удался.", channel.Name);
        }
    }

    // Reopens the current VOD stream with full context (quality/voiceover/
    // episode) from the given position; the retry budget is handled by callers
    private async Task ReopenVodAsync(TimeSpan resume, string reason)
    {
        var channel = _vodChannel!;
        var url = _lastStreamUrl!;
        var variants = _vodVariantUrls.Count > 0 ? new Dictionary<string, string>(_vodVariantUrls) : null;
        var quality = CurrentVodQuality;
        var playlist = OnlineCinemaPlaylist;
        var episodes = _vodEpisodes.Count > 0 ? _vodEpisodes.ToList() : null;
        var episodeIndex = CurrentVodEpisodeIndex;

        _logger.LogInformation(
            "VOD: переоткрытие потока ({Reason}) с позиции {Position}.",
            reason, resume);

        // StartPlaybackAsync must not consume the retry budget on this call
        _stallAutoRestart = true;
        try
        {
            await StartPlaybackAsync(channel, url, archiveEntry: null, isVod: true,
                vodVariants: variants, vodQuality: quality,
                resumePosition: resume > TimeSpan.Zero ? resume : null,
                vodEpisodes: episodes, vodEpisodeIndex: episodeIndex,
                onlineCinemaPlaylist: playlist, onlineCinemaLeafSwitch: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VOD: переоткрытие ({Reason}) не удалось.", reason);
        }
    }
}
