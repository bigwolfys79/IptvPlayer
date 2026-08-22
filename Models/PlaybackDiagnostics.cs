namespace IptvPlayer.Models;

/// <summary>
/// Снимок параметров текущего потока для оверлея статистики (Ctrl+J):
/// кодеки/разрешение/битрейт и выбранный декодер берутся из FFmpegMediaSource
/// один раз при открытии потока (StreamService), глубина буфера — из
/// конфигурации. Живые метрики (буферизация, простои) представление считает
/// само по событиям MediaPlayer. Снимок, а не живой объект источника —
/// чтобы оверлей не трогал FFmpeg-объекты, чьё время жизни привязано
/// к плееру (ConditionalWeakTable в StreamService).
/// </summary>
public sealed class PlaybackDiagnostics
{
    public string? VideoCodec { get; init; }
    public int VideoWidth { get; init; }
    public int VideoHeight { get; init; }
    public double FramesPerSecond { get; init; }
    public long VideoBitrate { get; init; }
    public FFmpegInteropX.DecoderEngine? VideoDecoderEngine { get; init; }
    public FFmpegInteropX.HardwareDecoderStatus? HardwareStatus { get; init; }
    public bool IsHdr { get; init; }

    public string? AudioCodec { get; init; }
    public int AudioChannels { get; init; }
    public string? AudioChannelLayout { get; init; }
    public int AudioSampleRate { get; init; }
    public long AudioBitrate { get; init; }

    public int ReadAheadSeconds { get; init; }
    public long ReadAheadBytes { get; init; }

    /// <summary>
    /// Скорость загрузки потока в битах в секунду (bps).
    /// Вычисляется по DownloadProgress FFmpegMediaSource.
    /// </summary>
    public long DownloadBitrate { get; set; }

    /// <summary>
    /// Поток открыт системным источником (откат, когда FFmpeg не смог) —
    /// статистика FFmpeg недоступна, оверлей показывает только это.
    /// </summary>
    public bool SystemSourceFallback { get; init; }
}
