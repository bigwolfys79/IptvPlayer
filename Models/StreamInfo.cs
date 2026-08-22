namespace IptvPlayer.Models;

public class StreamInfo
{
    public string ChannelId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public string? ErrorMessage { get; set; }
    public int Bitrate { get; set; }
    public DateTime LastChecked { get; set; }
}