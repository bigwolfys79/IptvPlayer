namespace IptvPlayer.Models;

public class Stream
{
    public string Url { get; set; } = string.Empty;
    public string? AudioTrack { get; set; }
    public string? SubtitleTrack { get; set; }
    public bool IsLive { get; set; }
}