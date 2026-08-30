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

        private readonly ILogger<StreamService> _logger;
        private readonly LocalStreamProxy _proxy;

        /// <summary>
        /// Скорость последнего открытого потока по счётчику байт прокси
        /// (бит/с) — null, если диагностический прокси выключен.
        /// </summary>
        public double? ProxyMeasuredBitrate => _proxy.Sample();

        /// <summary>
        /// Снимок параметров последнего открытого потока для оверлея
        /// статистики (Ctrl+J) — кодеки, разрешение, выбранный декодер,
        /// буфер. Обновляется в CreatePlayerAsync.
        /// </summary>
        public PlaybackDiagnostics? CurrentDiagnostics { get; private set; }

        public StreamService(ILogger<StreamService> logger, LocalStreamProxy proxy)
        {
            _logger = logger;
            _proxy = proxy;
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

        public async Task<MediaPlayer> CreatePlayerAsync(string streamUrl, PlaybackConfig streamConfig, bool isVod = false)
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

            try
            {
            var ffmpegConfig = new MediaSourceConfig();

                // Режим декодирования из настроек (переключается в диалоге
                // настроек): Hardware = GPU с автоматическим откатом на CPU
                // (Automatic), Software = принудительно процессор. Неверное
                // значение настроек трактуем как Software — рабочий по умолчанию.
                ffmpegConfig.Video.VideoDecoderMode =
                    string.Equals(streamConfig.DecoderMode, "Hardware", StringComparison.OrdinalIgnoreCase)
                        ? VideoDecoderMode.Automatic
                        : VideoDecoderMode.ForceFFmpegSoftwareDecoder;

                // DownmixAudioStreamsToStereo = false: НЕ сводим 5.1 в стерео
                // силами FFmpeg. Его downmix-коэффициенты дают заметно более
                // тихий звук, чем сведение аудиодвижком Windows, которое
                // использовалось раньше (все многоканальные каналы стали
                // тише). Многоканальный PCM уходит в Windows как есть —
                // система сводит его сама, как в системном плеере.
                ffmpegConfig.Audio.DownmixAudioStreamsToStereo = false;

                // Нормализация громкости (переключается в настройках):
                // тихие каналы подтягиваются к общему уровню.
                var normFilter = GetAudioFilters(streamConfig.AudioNormalization);
                if (!string.IsNullOrEmpty(normFilter))
                {
                    ffmpegConfig.Audio.FFmpegAudioFilters = normFilter;
                }

                // Упреждающая буферизация: провайдер отдаёт HLS сегментами по
                // 10 секунд, каждый сегмент (~5.7 МБ) качается 1-1.5 с. Без
                // буфера (по умолчанию он ВЫКЛЮЧЕН) плеер доигрывал сегмент и
                // простаивал эту секунду на каждом стыке — заметное
                // "подтормаживание каждые 10 секунд". Глубина буфера берётся
                // из настроек (слайдер "Буфер видео"): больше — плавнее на
                // нестабильной сети, но дальше от эфира. Размер подбирается с
                // запасом под 4K-битрейт (~4 МБ/с) и не меньше 32 МБ.
                var readAheadSeconds = Math.Clamp(streamConfig.ReadAheadSeconds, 5, 120);
                var readAheadBytes = Math.Max(32 * 1024 * 1024, readAheadSeconds * 4 * 1024 * 1024);
                if (isVod)
                {
                    // VOD (фильмы портала): эфирный буфер (15 с / 32+ МБ) на
                    // медленном CDN VOD держал старт потока по несколько
                    // секунд — плеер молча набивал буфер до порога. Здесь
                    // отдельная, меньшая глубина, настраиваемая независимо
                    // (VodReadAheadSeconds, слайдер «Буфер видеотеки»).
                    readAheadSeconds = Math.Clamp(streamConfig.VodReadAheadSeconds, 2, 15);
                    readAheadBytes = Math.Max(8 * 1024 * 1024, readAheadSeconds * 2 * 1024 * 1024);
                }

                ffmpegConfig.General.ReadAheadBufferEnabled = true;
                ffmpegConfig.General.ReadAheadBufferDuration = TimeSpan.FromSeconds(readAheadSeconds);
                ffmpegConfig.General.ReadAheadBufferSize = readAheadBytes;

                // HTTP-протокол FFmpeg: провайдер отдаёт сегменты попеременно
                // с двух серверов, поэтому keepalive-переиспользование
                // соединения ломается на КАЖДОМ сегменте ("keepalive request
                // failed ... retrying with new connection") — это лишняя
                // пауза перед каждым сегментом, а при медленном ретраите
                // затыкается и воспроизведение. multiple_requests=0 — сразу
                // новое соединение без обречённой попытки; reconnect* —
                // авто-восстановление при обрывах сети.
                ffmpegConfig.FFmpegOptions["multiple_requests"] = "0";
                // http_persistent — опция именно HLS-демуксера: он сам
                // включает keepalive для сегментов. Для VOD включаем
                // persistent connections — сегменты идут с одного CDN-
                // сервера, keepalive убирает оверхед TCP+TLS на каждом
                // сегменте и даёт буферу время набиться.
                ffmpegConfig.FFmpegOptions["http_persistent"] = isVod ? "1" : "0";
                ffmpegConfig.FFmpegOptions["reconnect"] = "1";
                ffmpegConfig.FFmpegOptions["reconnect_streamed"] = "1";
                ffmpegConfig.FFmpegOptions["reconnect_delay_max"] = "7";

                // Диагностический прокси (галка в настройках, по умолчанию
                // выкл.): FFmpeg качает через 127.0.0.1-посредника, который
                // считает байты — в Ctrl+J появляется реальная скорость.
                // Не-http(s) схемы (udp/rtmp) прокси не поддерживает.
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

                var ffmpegSource = await FFmpegMediaSource.CreateFromUriAsync(actualUrl, ffmpegConfig);
                player.Source = ffmpegSource.CreateMediaPlaybackItem();
                LiveSources.Add(player, ffmpegSource);
                if (!string.IsNullOrEmpty(normFilter))
                {
                    _logger.LogInformation(
                        "Поток открыт с аудиофильтром громкости: {Filter} (режим {Mode}) — тяжёлые фильтры могут влиять на плавность.",
                        normFilter, streamConfig.AudioNormalization);
                }
                CurrentDiagnostics = BuildDiagnostics(ffmpegSource, ffmpegConfig, normFilter);
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

        /// <summary>
        /// Снимок параметров для оверлея статистики: берётся один раз при
        /// открытии потока, чтобы потом не трогать живой FFmpegMediaSource
        /// (его время жизни привязано к плееру). CurrentVideoStream может
        /// быть ещё не выбран — тогда берётся первая дорожка.
        /// </summary>
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
                // Метаданные потока не критичны для воспроизведения.
                Serilog.Log.Debug(ex, "Не удалось собрать метаданные потока — оверлей получит пустую диагностику.");
                return new PlaybackDiagnostics();
            }
        }

        /// <summary>
        /// Диагностика URL потока: проверяет доступность и возвращает
        /// человекочитаемое описание проблемы.
        /// </summary>
        public async Task<string> DiagnoseStreamUrl(string? streamUrl)
        {
            if (string.IsNullOrWhiteSpace(streamUrl))
                return L.T("Url_Potoka_Pust");

            try
            {
                using var http = new System.Net.Http.HttpClient();
                http.Timeout = TimeSpan.FromSeconds(10);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("IptvPlayer/1.0");

                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, streamUrl);
                using var response = await http.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);

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
