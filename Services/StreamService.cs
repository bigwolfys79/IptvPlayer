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
                "Dynamic" => "dynaudnorm=f=30:g=5:m=12:p=0.95",
                "Loudness" when allowLoudness =>
                    "loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000,asetpts=N/SR/TB",
                "Loudness" => "dynaudnorm=f=30:g=5:m=12:p=0.95",
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

        public async Task<MediaPlayer> CreatePlayerAsync(string streamUrl, PlaybackConfig streamConfig, bool isVod = false)
        {
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

                var readAheadSeconds = Math.Clamp(streamConfig.ReadAheadSeconds, 5, 120);
                var readAheadBytes = Math.Max(32 * 1024 * 1024, readAheadSeconds * 4 * 1024 * 1024);
                if (isVod)
                {

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

                ffmpegConfig.FFmpegOptions["http_persistent"] = isVod ? "1" : "0";
                ffmpegConfig.FFmpegOptions["reconnect"] = "1";
                ffmpegConfig.FFmpegOptions["reconnect_streamed"] = "1";
                ffmpegConfig.FFmpegOptions["reconnect_delay_max"] = "7";

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
                        normFilter, audioNormalization);
                }
                CurrentDiagnostics = BuildDiagnostics(ffmpegSource, ffmpegConfig, normFilter);

                var videoStreams = ffmpegSource.VideoStreams.ToList();
                var audioStreams = ffmpegSource.AudioStreams.ToList();
                _logger.LogInformation(
                    "Поток открыт: видео дорожек {VCount}, аудио дорожек {ACount}{AudioDetail}.",
                    videoStreams.Count, audioStreams.Count,
                    audioStreams.Count > 0
                        ? " — " + string.Join(", ", audioStreams.Select(a =>
                            $"{a.CodecName} {a.ChannelLayout} {a.SampleRate}Hz {a.Bitrate/1000}kbps"))
                        : " (аудио не обнаружено)");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FFmpeg не смог открыть поток {Url}, откат на системный плеер.", streamUrl);
                player.Source = MediaSource.CreateFromUri(new Uri(streamUrl));

                CurrentDiagnostics = new PlaybackDiagnostics { SystemSourceFallback = true };
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
