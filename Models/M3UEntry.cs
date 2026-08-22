namespace IptvPlayer.Models;

public class M3UEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? GroupName { get; set; }
    public string? Url { get; set; }
    public string? Category { get; set; }
    public string? CoverUrl { get; set; }
}