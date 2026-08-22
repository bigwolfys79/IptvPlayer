namespace IptvPlayer.Models;

public class StreamConfig
{
    public string? AudioLanguage { get; set; }
    public string? SubtitleLanguage { get; set; }
    public int BufferTimeoutMs { get; set; } = 5000;
    public int MaxBitrate { get; set; }
}