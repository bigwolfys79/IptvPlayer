namespace IptvPlayer.Models;

public class Channel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Group { get; set; }
    public string? LogoUrl { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsHidden { get; set; }
}