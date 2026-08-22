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
    /// <summary>
    /// Создание плеера для IPTV-потока.
    ///
    /// Демуксинг и декодирование по умолчанию идут через FFmpegInteropX:
    /// системный HLS-стек Windows не поднимает HEVC-дорожку из MPEG-TS
    /// (каналы 4K играют только звук), а AC-3 начиная с Windows 11 24H2
    /// вообще убран из системы. FFmpeg разбирает TS и декодирует всё сам,
    /// рендер при этом остаётся штатный — MediaPlayerElement. Если FFmpeg
    /// не смог открыться (нет dll и т.п.) — откат на системный источник.
    /// </summary>
    public class StreamService : IStreamService
    {
        // FFmpegMediaSource обязан жить, пока жив созданный для него плеер:
        // MediaStreamSource получает сэмплы колбэками из этого объекта, и без
        // внешней ссылки GC собирал его посреди воспроизведения — картинка
        // шла рывками, звук пропадал, затем MediaFailed DecodingError
        // (0xC00D36B6) и крах процесса. ConditionalWeakTable держит значение
        // ровно столько, сколько жив ключ-плеер: утечек нет, при смене
        // канала (Dispose старого плеера) источник освобождается сам.
        private static readonly ConditionalWeakTable<MediaPlayer, FFmpegMediaSource> LiveSources = new();

        private readonly ISettingsService _settingsService;
        private readonly ILogger<StreamService> _logger;

        // Для измерения скорости загрузки
        private long _lastBitrateEstimate;

        /// <summary>
        /// Снимок параметров последнего открытого потока для оверлея
        /// статистики (Ctrl+J) — кодеки, разрешение, выбранный декодер,
        /// буфер. Обновляется в CreatePlayerAsync.
        /// </summary>
        public PlaybackDiagnostics? CurrentDiagnostics { get; private set; }

        public StreamService(ISettingsService settingsService, ILogger<StreamService> logger)
        {
            _settingsService = settingsService;
            _logger = logger;
        }

        // Цепочки нормализации громкости. Часть каналов в плейлисте
        // кодируется в разы тише остальных (пример — BCU TruMotion HD),
        // а MediaPlayer.Volume ограничен 100%: вытянуть такой канал можно
        // только фильтром в графе FFmpeg, до системного микшера.
        //  - dynaudnorm: динамически поднимает тихий звук (до ~+21 дБ),
        //    громкий почти не трогает; задержка — длина кадра (~0.3 с);
        //  - loudnorm: приводит любой канал к единой громкости EBU R128
        //    (−16 LUFS) — тише становится и громкие каналы, задержка ~3 с,
        //    поэтому после фильтра возвращаем привычные 48 кГц.
        private static string? GetAudioFilters(string? mode) => mode switch
        {
            "Dynamic" => "dynaudnorm=f=300:g=15:m=12:p=0.95",
            "Loudness" => "loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000",
            _ => null
        };

        /// <summary>
        /// Применяет нормализацию громкости к уже играющему плееру
        /// (переключение режима в настройках). Для плееров без FFmpeg-
        /// источника (системный откат) — тихо ничего не делает.
        /// </summary>
        public void ApplyAudioFilters(MediaPlayer? player, string? mode)
        {
            if (player is null || !LiveSources.TryGetValue(player, out var source))
            {
                return;
            }

            try
            {
                var filters = GetAudioFilters(mode);
                if (string.IsNullOrEmpty(filters))
                {
                    source.ClearFFmpegAudioFilters();
                }
                else
                {
                    source.SetFFmpegAudioFilters(filters);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось применить фильтр громкости.");
            }
        }

        public async Task<MediaPlayer> CreatePlayerAsync(string streamUrl)
        {
            MediaPlayer player;
            try
            {
                player = new MediaPlayer();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Не удалось создать плеер для потока '{streamUrl}'.", ex);
            }

            var settings = await _settingsService.LoadAsync();

            try
            {
                var config = new MediaSourceConfig();

                // Режим декодирования из настроек (переключается в диалоге
                // настроек): Hardware = GPU с автоматическим откатом на CPU
                // (Automatic), Software = принудительно процессор. Неверное
                // значение настроек трактуем как Software — рабочий по умолчанию.
                config.Video.VideoDecoderMode =
                    string.Equals(settings.DecoderMode, "Hardware", StringComparison.OrdinalIgnoreCase)
                        ? VideoDecoderMode.Automatic
                        : VideoDecoderMode.ForceFFmpegSoftwareDecoder;

                // DownmixAudioStreamsToStereo = false: НЕ сводим 5.1 в стерео
                // силами FFmpeg. Его downmix-коэффициенты дают заметно более
                // тихий звук, чем сведение аудиодвижком Windows, которое
                // использовалось раньше (все многоканальные каналы стали
                // тише). Многоканальный PCM уходит в Windows как есть —
                // система сводит его сама, как в системном плеере.
                config.Audio.DownmixAudioStreamsToStereo = false;

                // Нормализация громкости (переключается в настройках):
                // тихие каналы подтягиваются к общему уровню.
                var normFilter = GetAudioFilters(settings.AudioNormalization);
                if (!string.IsNullOrEmpty(normFilter))
                {
                    config.Audio.FFmpegAudioFilters = normFilter;
                }

                // Упреждающая буферизация: провайдер отдаёт HLS сегментами по
                // 10 секунд, каждый сегмент (~5.7 МБ) качается 1-1.5 с. Без
                // буфера (по умолчанию он ВЫКЛЮЧЕН) плеер доигрывал сегмент и
                // простаивал эту секунду на каждом стыке — заметное
                // "подтормаживание каждые 10 секунд". Глубина буфера берётся
                // из настроек (слайдер "Буфер видео"): больше — плавнее на
                // нестабильной сети, но дальше от эфира. Размер подбирается с
                // запасом под 4K-битрейт (~4 МБ/с) и не меньше 32 МБ.
                var readAheadSeconds = Math.Clamp(settings.ReadAheadSeconds, 5, 120);
                config.General.ReadAheadBufferEnabled = true;
                config.General.ReadAheadBufferDuration = TimeSpan.FromSeconds(readAheadSeconds);
                config.General.ReadAheadBufferSize = Math.Max(
                    32 * 1024 * 1024,
                    readAheadSeconds * 4 * 1024 * 1024);

                // HTTP-протокол FFmpeg: провайдер отдаёт сегменты попеременно
                // с двух серверов, поэтому keepalive-переиспользование
                // соединения ломается на КАЖДОМ сегменте ("keepalive request
                // failed ... retrying with new connection") — это лишняя
                // пауза перед каждым сегментом, а при медленном ретраите
                // затыкается и воспроизведение. multiple_requests=0 — сразу
                // новое соединение без обречённой попытки; reconnect* —
                // авто-восстановление при обрывах сети.
                config.FFmpegOptions["multiple_requests"] = "0";
                // http_persistent — опция именно HLS-демуксера: он сам
                // включает keepalive для сегментов, multiple_requests на
                // внешний контекст не влияет.
                config.FFmpegOptions["http_persistent"] = "0";
                config.FFmpegOptions["reconnect"] = "1";
                config.FFmpegOptions["reconnect_streamed"] = "1";
                config.FFmpegOptions["reconnect_delay_max"] = "7";

                var ffmpegSource = await FFmpegMediaSource.CreateFromUriAsync(streamUrl, config);
                player.Source = ffmpegSource.CreateMediaPlaybackItem();
                LiveSources.Add(player, ffmpegSource);
                CurrentDiagnostics = BuildDiagnostics(ffmpegSource, config);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FFmpeg не смог открыть поток {Url}, откат на системный плеер.", streamUrl);
                player.Source = MediaSource.CreateFromUri(new Uri(streamUrl));

                // Системный источник не отдаёт кодеки/декодер — оверлей
                // статистики покажет только факт отката.
                CurrentDiagnostics = new PlaybackDiagnostics { SystemSourceFallback = true };
            }

            player.Play();
            return player;
        }

        public Task<StreamInfo> GetStreamInfoAsync(string streamUrl)
        {
            var info = new StreamInfo
            {
                ChannelId = "1",
                Url = streamUrl,
                IsAvailable = true,
                Bitrate = 5000,
                LastChecked = DateTime.Now
            };

            return Task.FromResult(info);
        }

        /// <summary>
        /// Обновляет оценку скорости загрузки потока.
        /// Для live-потоков битрейт часто не указан в метаданных, поэтому
        /// оцениваем скорость по разрешению и FPS (эмпирические формулы).
        /// </summary>
        public void UpdateDownloadSpeed(MediaPlayer? player)
        {
            if (CurrentDiagnostics == null)
            {
                return;
            }

            try
            {
                // Если битрейт известен из метаданных — используем его
                var totalBitrate = CurrentDiagnostics.VideoBitrate + CurrentDiagnostics.AudioBitrate;
                if (totalBitrate > 0)
                {
                    CurrentDiagnostics.DownloadBitrate = (long)(totalBitrate * 1.1); // +10% overhead
                    return;
                }

                // Иначе оцениваем по разрешению и FPS (эмпирическая формула)
                // FHD 60fps ≈ 8-12 Mbps, HD 30fps ≈ 3-5 Mbps и т.д.
                var width = CurrentDiagnostics.VideoWidth;
                var height = CurrentDiagnostics.VideoHeight;
                var fps = CurrentDiagnostics.FramesPerSecond;

                if (width > 0 && height > 0)
                {
                    // Базовый битрейт по разрешению (пиксели в секунду)
                    var pixelsPerSecond = width * height * Math.Max(fps, 30);

                    // Эмпирический коэффициент: ~0.1-0.15 бит на пиксель для H.264/H.265
                    // Зависит от эффективности кодека (H.265 ~30% эффективнее H.264)
                    var codec = CurrentDiagnostics.VideoCodec?.ToLowerInvariant() ?? "";
                    var bitsPerPixel = codec.Contains("265") || codec.Contains("hevc") ? 0.10 : 0.12;

                    var estimatedVideoBitrate = (long)(pixelsPerSecond * bitsPerPixel);

                    // Добавляем аудио битрейт (если известен) или типичное значение
                    var audioBitrate = CurrentDiagnostics.AudioBitrate > 0
                        ? CurrentDiagnostics.AudioBitrate
                        : 192_000; // 192 kbps типичный для AAC

                    // Сглаживание оценок
                    var newEstimate = (long)((estimatedVideoBitrate + audioBitrate) * 1.15); // +15% overhead TS
                    _lastBitrateEstimate = _lastBitrateEstimate == 0
                        ? newEstimate
                        : (_lastBitrateEstimate * 7 + newEstimate * 3) / 10; // EMA

                    CurrentDiagnostics.DownloadBitrate = _lastBitrateEstimate;
                }
            }
            catch (Exception ex)
            {
                // Игнорируем ошибки при измерении скорости
                Serilog.Log.Debug(ex, "Измерение скорости скачивания прервано ошибкой.");
            }
        }

        /// <summary>
        /// Снимок параметров для оверлея статистики: берётся один раз при
        /// открытии потока, чтобы потом не трогать живой FFmpegMediaSource
        /// (его время жизни привязано к плееру). CurrentVideoStream может
        /// быть ещё не выбран — тогда берётся первая дорожка.
        /// </summary>
        private static PlaybackDiagnostics BuildDiagnostics(
            FFmpegMediaSource source, MediaSourceConfig config)
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
                    ReadAheadBytes = config.General.ReadAheadBufferSize
                };
            }
            catch (Exception ex)
            {
                // Метаданные потока не критичны для воспроизведения.
                Serilog.Log.Debug(ex, "Не удалось собрать метаданные потока — оверлей получит пустую диагностику.");
                return new PlaybackDiagnostics();
            }
        }
    }
}
