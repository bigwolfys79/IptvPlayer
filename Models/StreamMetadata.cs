namespace IptvPlayer.Models;

public class StreamMetadata
{
    public string Url { get; set; } = string.Empty;
    public string? AudioCodec { get; set; }
    public string? VideoCodec { get; set; }
    public int Bitrate { get; set; }
    public int BufferSize { get; set; } = 60000;
    public string? StreamUrl { get; set; }
}