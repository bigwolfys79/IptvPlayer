namespace IptvPlayer.Models;

public class CacheEntry
{
    public string Url { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime LastAccessed { get; set; }
}