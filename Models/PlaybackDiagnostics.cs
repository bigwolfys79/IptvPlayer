namespace IptvPlayer.Models;


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


    public string? AudioFilter { get; init; }


    public bool SystemSourceFallback { get; init; }
}
