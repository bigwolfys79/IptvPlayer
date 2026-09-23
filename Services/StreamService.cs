using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FFmpegInteropX;
using Windows.Media.Core;
using Windows.Media.Playback;
using IptvPlayer.Models;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services
{


    public class StreamService : IStreamService
    {

        private static readonly ConditionalWeakTable<MediaPlayer, FFmpegMediaSource> LiveSources = new();

        private static readonly ConditionalWeakTable<MediaPlayer, MediaPlaybackItem> PlaybackItems = new();

        private static readonly ConditionalWeakTable<MediaPlayer, Dictionary<string, string>> VodMasterVariants = new();

        private static readonly HttpClient PlaylistHttpClient = CreatePlaylistHttpClient();

        private static HttpClient CreatePlaylistHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) IptvPlayer/1.0");
            return client;
        }

        private readonly ILogger<StreamService> _logger;
        private readonly LocalStreamProxy _proxy;


        public double? ProxyMeasuredBitrate => _proxy.Sample();


        public PlaybackDiagnostics? CurrentDiagnostics { get; private set; }

        public StreamService(ILogger<StreamService> logger, LocalStreamProxy proxy)
        {
            _logger = logger;
            _proxy = proxy;
        }

        internal static string? GetAudioFilters(string? mode, bool allowLoudness, int boostPercent = 100)
        {
            var baseFilter = mode switch
            {
                "Dynamic" => "dynaudnorm=f=150:g=5:m=12:p=0.95",
                "Loudness" when allowLoudness =>
                    "loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000,asetpts=N/SR/TB",
                "Loudness" => "dynaudnorm=f=150:g=5:m=12:p=0.95",
                _ => null
            };

            if (boostPercent > 100)
            {
                var gain = (boostPercent / 100.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                var boostFilter = $"volume={gain}";
                return string.IsNullOrEmpty(baseFilter) ? boostFilter : $"{baseFilter},{boostFilter}";
            }

            return baseFilter;
        }


        // Picks the first audio track matching the user's preferred language order ("rus,ukr,eng"); -1 keeps the stream default
        public static int SelectPreferredAudioIndex(
            IReadOnlyList<string?> languages, string? preferred, IReadOnlySet<int>? invalidIndexes = null)
        {
            if (string.IsNullOrWhiteSpace(preferred) || languages.Count == 0)
            {
                return -1;
            }

            var prefs = preferred
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeLanguageCode)
                .Where(c => !string.IsNullOrEmpty(c))
                .Select(c => c!)
                .ToList();
            if (prefs.Count == 0)
            {
                return -1;
            }

            var normalized = languages.Select(NormalizeLanguageCode).ToList();
            foreach (var pref in prefs)
            {
                for (var i = 0; i < normalized.Count; i++)
                {
                    if (invalidIndexes != null && invalidIndexes.Contains(i))
                    {
                        continue;
                    }

                    if (normalized[i] is { } lang && lang.StartsWith(pref, StringComparison.Ordinal))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }


        private static string? NormalizeLanguageCode(string? lang)
        {
            if (string.IsNullOrWhiteSpace(lang))
            {
                return null;
            }

            var token = lang.Trim().ToLowerInvariant().Split('-', '_')[0];
            return token switch
            {
                "ru" or "rus" or "russian" => "rus",
                "uk" or "ua" or "ukr" or "ukrainian" => "ukr",
                "en" or "eng" or "english" => "eng",
                "de" or "deu" or "german" => "deu",
                "fr" or "fra" or "french" => "fra",
                "es" or "esp" or "spa" => "spa",
                "it" or "ita" => "ita",
                "pl" or "pol" => "pol",
                "kk" or "kz" or "kaz" => "kaz",
                "und" or "mis" => null,
                _ => token
            };
        }


        public IReadOnlyList<(int Index, string? Language)> GetAudioTracks(MediaPlayer? player)
        {
            if (player is null || !PlaybackItems.TryGetValue(player, out var item))
            {
                return Array.Empty<(int, string?)>();
            }

            var tracks = item.AudioTracks;
            var result = new List<(int, string?)>(tracks.Count);
            for (var i = 0; i < tracks.Count; i++)
            {
                result.Add((i, tracks[i].Language));
            }

            return result;
        }


        public int GetSelectedAudioTrackIndex(MediaPlayer? player)
        {
            if (player is null || !PlaybackItems.TryGetValue(player, out var item))
            {
                return -1;
            }

            return item.AudioTracks.SelectedIndex;
        }


        public bool TrySelectAudioTrack(MediaPlayer? player, int index)
        {
            if (player is null || !PlaybackItems.TryGetValue(player, out var item))
            {
                return false;
            }

            var tracks = item.AudioTracks;
            if (index < 0 || index >= tracks.Count)
            {
                return false;
            }

            tracks.SelectedIndex = index;
            return true;
        }


        public bool TryGetVodMasterVariants(MediaPlayer? player, out Dictionary<string, string> variants)
        {
            if (player is null || !VodMasterVariants.TryGetValue(player, out var stored))
            {
                variants = new Dictionary<string, string>();
                return false;
            }

            variants = stored;
            return variants.Count > 0;
        }


        // Portal episodes often expose a single HLS master URL (no portal-side variants dict);
        // parse the master playlist so the quality switcher can offer its renditions
        private async Task ProbeVodMasterVariantsAsync(MediaPlayer player, string streamUrl)
        {
            if (!streamUrl.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                var playlist = await PlaylistHttpClient.GetStringAsync(streamUrl);
                var variants = ParseHlsMasterVariants(playlist, new Uri(streamUrl));
                if (variants.Count > 0)
                {
                    VodMasterVariants.AddOrUpdate(player, variants);
                    _logger.LogInformation(
                        "Портал/VOD: из мастер-плейлиста извлечено вариантов качества: {Count}.",
                        variants.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VOD: не удалось разобрать мастер-плейлист {Url}.",
                    SecretProtector.Mask(streamUrl));
            }
        }


        public static Dictionary<string, string> ParseHlsMasterVariants(string playlist, Uri baseUrl)
        {
            var variants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(playlist) || !playlist.StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                return variants;
            }

            string? resolution = null;
            foreach (var rawLine in playlist.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
                {
                    resolution = ParseHlsResolution(line);
                    continue;
                }

                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (resolution is not null && TryResolveUri(line, baseUrl, out var uri))
                {
                    // Multiple renditions may share one resolution — first wins
                    if (!variants.ContainsKey(resolution))
                    {
                        variants[resolution] = uri;
                    }
                    resolution = null;
                }
            }

            return variants;
        }


        private static string? ParseHlsResolution(string streamInfLine)
        {
            var marker = "RESOLUTION=";
            var start = streamInfLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            var end = start;
            while (end < streamInfLine.Length && (char.IsDigit(streamInfLine[end]) || streamInfLine[end] == 'x'))
            {
                end++;
            }

            var value = streamInfLine[start..end];
            var parts = value.Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height))
            {
                return null;
            }

            return height > 0 ? $"{height}p" : null;
        }


        private static bool TryResolveUri(string line, Uri baseUrl, out string resolved)
        {
            try
            {
                resolved = new Uri(baseUrl, line).ToString();
                return true;
            }
            catch (UriFormatException)
            {
                resolved = string.Empty;
                return false;
            }
        }


        public void ApplyAudioFilters(MediaPlayer? player, string? mode, bool allowLoudness = false, int boostPercent = 100)
        {
            if (player is null || !LiveSources.TryGetValue(player, out var source))
            {
                return;
            }

            try
            {
                var filters = GetAudioFilters(mode, allowLoudness, boostPercent);
                if (string.IsNullOrEmpty(filters))
                {
                    source.ClearFFmpegAudioFilters();
                }
                else
                {
                    if (mode == "Loudness" && !allowLoudness)
                    {
                        _logger.LogInformation(
                            "Loudness на живом эфире отстаёт от видео (~3 с буфера loudnorm) — применён Dynamic.");
                    }
                    source.SetFFmpegAudioFilters(filters);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось применить фильтр громкости.");
            }
        }


        public void ApplyVideoFilters(MediaPlayer? player, string? mode)
        {
            if (player is null || !LiveSources.TryGetValue(player, out var source))
            {
                return;
            }

            var filters = VideoUpscaler.GetFilters(mode);
            try
            {
                if (string.IsNullOrEmpty(filters))
                {
                    source.ClearFFmpegVideoFilters();
                }
                else
                {
                    source.SetFFmpegVideoFilters(filters);
                }
                CurrentVideoFilter = filters;

                var readback = source.CurrentVideoStream is { } vs
                    ? source.GetFFmpegVideoFilters(vs)
                    : null;
                _logger.LogInformation(
                    "Применён пресет улучшения картинки {Mode}: {Filters} (подтверждено источником: {Readback})",
                    mode, filters ?? "(выкл)",
                    string.IsNullOrEmpty(readback) ? "(выкл)" : readback);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Не удалось применить видео-фильтры {Mode} ({Filters}) к текущему потоку.",
                    mode, filters);
            }
        }


        public string? CurrentVideoFilter { get; private set; }

        // Serialized background teardown: off the UI thread, but a new stream open
        // waits for the previous player's native dispose to finish
        private System.Threading.Tasks.Task _disposeQueue = System.Threading.Tasks.Task.CompletedTask;

        public System.Threading.Tasks.Task DisposeQueue => _disposeQueue;

        public void ReleasePlayer(MediaPlayer? player)
        {
            if (player is null)
            {
                return;
            }

            LiveSources.TryGetValue(player, out var source);
            LiveSources.Remove(player);
            try
            {
                player.Pause();
                player.Source = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ReleasePlayer: не удалось отвязать источник.");
            }

            var prev = _disposeQueue;
            _disposeQueue = System.Threading.Tasks.Task.Run(async () =>
            {
                // Serialize teardown: wait for the previous player's dispose first
                try
                {
                    await prev.ConfigureAwait(false);
                }
                catch
                {
                    // Previous teardown already logged its own failure
                }

                var teardown = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    source?.Dispose();
                    player.Dispose();
                    teardown.Stop();
                    _logger.LogInformation("ReleasePlayer: нативный teardown занял {Ms:F0} мс (фон).",
                        teardown.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ReleasePlayer: не удалось освободить плеер.");
                }
            });
        }


        public async Task<MediaPlayer> CreatePlayerAsync(string streamUrl, PlaybackConfig streamConfig, bool isVod = false, CancellationToken ct = default)
        {
            // Hard cap for the whole open: a silent dead server blocks the native read forever
            using var openCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
            openCts.CancelAfter(TimeSpan.FromSeconds(45));
            var openCt = openCts.Token;

            MediaPlayer player;
            try
            {
                player = new MediaPlayer();

                player.IsVideoFrameServerEnabled = streamConfig.FrameServer;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Не удалось создать плеер для потока '{streamUrl}'.", ex);
            }

            FFmpegMediaSource? createdSource = null;
            try
            {
            var ffmpegConfig = new MediaSourceConfig();

                ffmpegConfig.Video.VideoDecoderMode =
                    string.Equals(streamConfig.DecoderMode, "Hardware", StringComparison.OrdinalIgnoreCase)
                        ? VideoDecoderMode.Automatic
                        : VideoDecoderMode.ForceFFmpegSoftwareDecoder;

                ffmpegConfig.Audio.DownmixAudioStreamsToStereo = false;

                var isLocalFile = streamUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    || (streamUrl.Length >= 2 && streamUrl[1] == ':');

                var allowLoudness = isVod || isLocalFile;
                var audioNormalization = streamConfig.AudioNormalization;
                if (audioNormalization == "Loudness" && !allowLoudness)
                {
                    _logger.LogInformation(
                        "Loudness на живом эфире отстаёт от видео (~3 с буфера loudnorm) — применён Dynamic.");
                    audioNormalization = "Dynamic";
                }
                var normFilter = GetAudioFilters(
                    audioNormalization, allowLoudness,
                    Math.Clamp(streamConfig.AudioVolumeBoost, 100, 200));
                if (!string.IsNullOrEmpty(normFilter))
                {
                    ffmpegConfig.Audio.FFmpegAudioFilters = normFilter;
                }

                var upscalerMode = VideoUpscaler.Normalize(streamConfig.VideoUpscaler);
                var videoFilter = VideoUpscaler.GetFilters(upscalerMode);
                if (!string.IsNullOrEmpty(videoFilter))
                {
                    ffmpegConfig.Video.FFmpegVideoFilters = videoFilter;
                }

                // Live: пол 2 с — минимум, удерживающий целый GOP (≈1–2 с у IPTV).
                // Одна секунда не даёт запаса на джиттер; пяти не нужно — это уже
                // слышимая задержка при переключении каналов.
                var readAheadSeconds = Math.Clamp(streamConfig.ReadAheadSeconds, 2, 120);

                // Байтовый потолок считаем из расчёта 4 МБ/с — покрывает 4K HEVC
                // (~32 Мбит/с) без запаса впустую. SD/HD-каналы до него не дорастут,
                // поэтому цена для них нулевая. Верхняя граница 24 МБ защищает память
                // при большом ReadAheadSeconds; нижняя 4 МБ — минимум для надёжного
                // буферирования пика на любом качестве.
                // ReadAheadBufferSize — это потолок, а не преаллокация: FFmpegInteropX
                // не заполняет его до краёв перед первым кадром, задержку на старте
                // он не вносит. Узкий же буфер на 4K-канале вызывает постоянные паузы
                // фонового чтения и фризы на сетевом джиттере.
                var readAheadBytes = Math.Clamp(readAheadSeconds * 4 * 1024 * 1024,
                                                4 * 1024 * 1024, 24 * 1024 * 1024);
                if (isVod)
                {
                    // VOD: можно буферировать щедрее — латентность не критична,
                    // зато нужна плавность при перемотке.
                    readAheadSeconds = Math.Clamp(streamConfig.VodReadAheadSeconds, 2, 15);
                    readAheadBytes = Math.Max(8 * 1024 * 1024, readAheadSeconds * 2 * 1024 * 1024);
                }

                ffmpegConfig.General.ReadAheadBufferEnabled = !isLocalFile;
                if (!isLocalFile)
                {
                    ffmpegConfig.General.ReadAheadBufferDuration = TimeSpan.FromSeconds(readAheadSeconds);
                    ffmpegConfig.General.ReadAheadBufferSize = readAheadBytes;
                }

                ffmpegConfig.FFmpegOptions["multiple_requests"] = "0";

                // Сокращаем зондирование потока для live-каналов.
                // По умолчанию FFmpeg анализирует до 5 МБ / 5 с перед первым кадром —
                // именно это даёт ~1–1,5 с задержки на старте. 500 КБ / 500 мс
                // достаточно для любого IPTV-формата (TS, HLS, RTMP): параметры
                // видео/аудио всегда попадают в первые PAT/PMT-пакеты.
                // fpsprobesize=2 вместо 20 по умолчанию — быстрее определяем fps,
                // не ждём 20 кадров.
                // Для VOD оставляем дефолты: там важна точность, а не скорость старта.
                if (!isVod)
                {
                    ffmpegConfig.FFmpegOptions["probesize"] = "500000";
                    ffmpegConfig.FFmpegOptions["analyzeduration"] = "500000";
                    ffmpegConfig.FFmpegOptions["fpsprobesize"] = "2";
                }

                // http_persistent=1 позволяет переиспользовать TCP/TLS-соединение
                // между переключениями каналов — срезает 100–150 мс на холодном старте.
                // Если сервер не поддерживает keep-alive и разрывает соединение сам,
                // FFmpeg корректно переоткроет его (reconnect=1 ниже подхватит).
                // При проблемах с конкретным поставщиком — вернуть isVod ? "1" : "0".
                ffmpegConfig.FFmpegOptions["http_persistent"] = "1";
                ffmpegConfig.FFmpegOptions["reconnect"] = "1";
                ffmpegConfig.FFmpegOptions["reconnect_streamed"] = "1";
                ffmpegConfig.FFmpegOptions["reconnect_delay_max"] = "7";
                // Cap a single blocking connect/read (15 s, microseconds): a server that
                // accepts TCP but never answers otherwise hangs the open until app restart
                ffmpegConfig.FFmpegOptions["rw_timeout"] = "15000000";

                var actualUrl = streamUrl;
                if (streamConfig.DiagnosticProxy
                    && (streamUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || streamUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    _proxy.ResetForNewStream();
                    actualUrl = _proxy.WrapUrl(streamUrl);
                    _logger.LogInformation(
                        "Диагностический прокси включён: поток идёт через {Local}.",
                        actualUrl);
                }

                openCt.ThrowIfCancellationRequested();
                try
                {
                    // New open must not wait forever on a stuck background teardown
                    await _disposeQueue.WaitAsync(TimeSpan.FromSeconds(15), openCt)
                        .ConfigureAwait(continueOnCapturedContext: false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Dispose-очередь не освободилась за 15 с — открываю поток, не дожидаясь.");
                }
                var ffmpegSource = createdSource =
                    await FFmpegMediaSource.CreateFromUriAsync(actualUrl, ffmpegConfig).AsTask(openCt);
                var videoStreams = ffmpegSource.VideoStreams.ToList();
                var audioStreams = ffmpegSource.AudioStreams.ToList();

                // Некоторые потоки отдают "пустые" аудиодорожки (0 каналов / 0 Гц).
                // Демультиплексор FFmpeg постоянно спотыкается об их метаданные и
                // спамит ошибками чтения, из-за чего плеер заикается. Если среди
                // дорожек есть валидные — переключаемся на первую из них до того,
                // как поток уйдёт в плеер; если валидных нет — оставляем как есть,
                // отключать здесь нечего.
                var mediaPlaybackItem = ffmpegSource.CreateMediaPlaybackItem();
                var invalidAudioIndexes = audioStreams
                    .Select((a, idx) => (a, idx))
                    .Where(t => t.a.Channels <= 0 || t.a.SampleRate <= 0)
                    .Select(t => t.idx)
                    .ToList();
                if (invalidAudioIndexes.Count > 0)
                {
                    var audioTracks = mediaPlaybackItem.AudioTracks;
                    var currentIndex = audioTracks.SelectedIndex;
                    if (currentIndex >= 0 && invalidAudioIndexes.Contains(currentIndex))
                    {
                        var validIndex = Enumerable.Range(0, audioStreams.Count)
                            .FirstOrDefault(i => !invalidAudioIndexes.Contains(i), -1);
                        if (validIndex >= 0)
                        {
                            audioTracks.SelectedIndex = validIndex;
                        }
                    }
                    // LogDebug, а не Warning: пустые дорожки — известный паттерн
                    // ряда поставщиков (поток с двумя AAC, вторая без метаданных).
                    // Мы уже переключились на валидную дорожку выше; предупреждать
                    // при каждом открытии канала нет смысла, лог засоряется.
                    _logger.LogDebug(
                        "Аудиодорожки с пустыми метаданными (0 каналов/0 Гц): {Indexes} — переключение выполнено.",
                        string.Join(", ", invalidAudioIndexes));
                }

                // Auto-select the preferred audio language when the stream provides language metadata
                var invalidSet = invalidAudioIndexes.Count > 0 ? invalidAudioIndexes.ToHashSet() : null;
                var preferredAudioIndex = SelectPreferredAudioIndex(
                    audioStreams.Select(a => (string?)a.Language).ToList(),
                    streamConfig.PreferredAudioLanguage,
                    invalidSet);
                if (preferredAudioIndex >= 0 && preferredAudioIndex != mediaPlaybackItem.AudioTracks.SelectedIndex)
                {
                    mediaPlaybackItem.AudioTracks.SelectedIndex = preferredAudioIndex;
                    _logger.LogInformation(
                        "Аудиодорожка: выбран язык {Language} (дорожка {Index} из {Count}).",
                        streamConfig.PreferredAudioLanguage, preferredAudioIndex + 1, audioStreams.Count);
                }

                player.Source = mediaPlaybackItem;

                LiveSources.Add(player, ffmpegSource);
                PlaybackItems.AddOrUpdate(player, mediaPlaybackItem);
                if (isVod)
                {
                    await ProbeVodMasterVariantsAsync(player, streamUrl);
                }
                if (!string.IsNullOrEmpty(normFilter))
                {
                    _logger.LogInformation(
                        "Поток открыт с аудиофильтром громкости: {Filter} (режим {Mode}) — тяжёлые фильтры могут влиять на плавность.",
                        normFilter, audioNormalization);
                }
                CurrentDiagnostics = BuildDiagnostics(ffmpegSource, ffmpegConfig, normFilter);

                _logger.LogInformation(
                    "Поток открыт: видео дорожек {VCount}, аудио дорожек {ACount}{AudioDetail}.",
                    videoStreams.Count, audioStreams.Count,
                    audioStreams.Count > 0
                        ? " — " + string.Join(", ", audioStreams.Select(a =>
                        {
                            // Ряд поставщиков не кладёт битрейт в заголовки TS —
                            // FFmpeg возвращает 0. Показываем прочерк вместо «0kbps».
                            var br = a.Bitrate > 0 ? $"{a.Bitrate / 1000}kbps" : "?kbps";
                            var lang = string.IsNullOrWhiteSpace(a.Language) ? null : $" {a.Language}";
                            return $"{a.CodecName} {a.ChannelLayout} {a.SampleRate}Hz {br}{lang}";
                        }))
                        : " (аудио не обнаружено)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Source may exist when the failure hit after CreateFromUriAsync
                try { createdSource?.Dispose(); } catch { }

                _logger.LogWarning(ex, "FFmpeg не смог открыть поток {Url}, откат на системный плеер.", streamUrl);
                try
                {
                    player.Source = MediaSource.CreateFromUri(new Uri(streamUrl));
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogWarning(fallbackEx,
                        "Откат на системный плеер не удался для {Url} — возвращаем плеер без источника.", streamUrl);
                }

                CurrentDiagnostics = new PlaybackDiagnostics { SystemSourceFallback = true };
            }
            catch (OperationCanceledException)
            {
                // Cancelled open: the player never reaches the caller — dispose here
                // or the native resources leak on every channel zap
                try { createdSource?.Dispose(); } catch { }
                try { player.Dispose(); } catch { }
                throw;
            }

            player.Play();
            return player;
        }


        private static PlaybackDiagnostics BuildDiagnostics(
            FFmpegMediaSource source, MediaSourceConfig config, string? audioFilter = null)
        {
            try
            {
                var video = source.CurrentVideoStream ?? source.VideoStreams.FirstOrDefault();
                var audio = source.CurrentAudioStream ?? source.AudioStreams.FirstOrDefault();

                return new PlaybackDiagnostics
                {
                    VideoCodec = video?.CodecName,
                    VideoWidth = video?.PixelWidth ?? 0,
                    VideoHeight = video?.PixelHeight ?? 0,
                    FramesPerSecond = video?.FramesPerSecond ?? 0,
                    VideoBitrate = video?.Bitrate ?? 0,
                    VideoDecoderEngine = video?.DecoderEngine,
                    HardwareStatus = video?.HardwareDecoderStatus,
                    IsHdr = video?.IsHdrActive ?? false,
                    AudioCodec = audio?.CodecName,
                    AudioChannels = audio?.Channels ?? 0,
                    AudioChannelLayout = audio?.ChannelLayout,
                    AudioSampleRate = audio?.SampleRate ?? 0,
                    AudioBitrate = audio?.Bitrate ?? 0,
                    ReadAheadSeconds = (int)config.General.ReadAheadBufferDuration.TotalSeconds,
                    ReadAheadBytes = config.General.ReadAheadBufferSize,
                    AudioFilter = audioFilter
                };
            }
            catch (Exception ex)
            {

                Serilog.Log.Debug(ex, "Не удалось собрать метаданные потока — оверлей получит пустую диагностику.");
                return new PlaybackDiagnostics();
            }
        }


        // Shared client: avoids TCP/TLS setup and socket exhaustion per call
        private static readonly System.Net.Http.HttpClient DiagnosticHttpClient = CreateDiagnosticHttpClient();

        private static System.Net.Http.HttpClient CreateDiagnosticHttpClient()
        {
            var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("IptvPlayer/1.0");
            return http;
        }

        public async Task<string> DiagnoseStreamUrl(string? streamUrl)
        {
            if (string.IsNullOrWhiteSpace(streamUrl))
                return L.T("Url_Potoka_Pust");


            if (streamUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                (streamUrl.Length >= 2 && streamUrl[1] == ':'))
            {
                return File.Exists(streamUrl) || File.Exists(new Uri(streamUrl).LocalPath)
                    ? L.T("Diag_200_OK_No_Decode")
                    : "Файл не найден на диске";
            }

            try
            {
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, streamUrl);
                using var response = await DiagnosticHttpClient.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);

                var status = (int)response.StatusCode;
                if (status == 200)
                    return L.T("Diag_200_OK_No_Decode");

                if (status == 403)
                    return L.T("Diag_403_Zapreshchen");

                if (status == 404)
                    return L.T("Diag_404_Nayden");

                if (status == 502 || status == 503)
                    return string.Format(L.T("Diag_502_503_Nedostupen"), status);

                if (status >= 500)
                    return string.Format(L.T("Diag_500_Oshibka"), status);

                return string.Format(L.T("Diag_Status_Kod_0"), status);
            }
            catch (TaskCanceledException)
            {
                return L.T("Diag_Taymaut_10s");
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                return string.Format(L.T("Diag_Ne_Podklyuchitsya_0"), ex.Message);
            }
            catch (Exception ex)
            {
                return string.Format(L.T("Diag_Oshibka_0"), ex.Message);
            }
        }
    }
}
