namespace IptvPlayer.Services;

using System.Collections.Generic;


// One film card scraped from an online-cinema category page
public class OnlineCinemaItem
{
    public string PageUrl { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public int Year { get; set; }

    public string PosterUrl { get; set; } = string.Empty;

    public List<string> Genres { get; set; } = new();

    public string Description { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;
}


// Persisted marker: which pages of a category are already in the catalog DB
public class OnlineCinemaPageInfo
{
    public string Category { get; set; } = string.Empty;

    public int PageNumber { get; set; }

    public int TotalPages { get; set; }

    public DateTime LoadedAtUtc { get; set; }
}


// Stream resolved on demand from a film page
public class OnlineCinemaStream
{
    public string Label { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    // Renditions parsed from the master playlist ("1080p" -> url, "Авто" -> master);
    // empty when the resolved link is a media playlist
    public Dictionary<string, string> Variants { get; } = new();

    // Series → seasons/episodes/voiceovers; films → Voiceovers only (null Seasons)
    public OnlineCinemaPlaylist? Playlist { get; set; }
}


// Voiceover leaf of the playlist tree: Data is the api payload for lazy
// resolution; ResolvedUrl is set when the leaf is already resolved
public class OnlineCinemaLeaf
{
    public string Label { get; set; } = string.Empty;

    public string Data { get; set; } = string.Empty;

    public string Origin { get; set; } = string.Empty;

    public string EmbedUrl { get; set; } = string.Empty;

    public string? ResolvedUrl { get; set; }

    // Renditions parsed from ResolvedUrl's master playlist; cached so a leaf
    // switch plays a media playlist, not the multi-program master
    public Dictionary<string, string> Variants { get; } = new();
}


public class OnlineCinemaEpisode
{
    public string Label { get; set; } = string.Empty;

    public List<OnlineCinemaLeaf> Voiceovers { get; } = new();
}


public class OnlineCinemaSeason
{
    public string Label { get; set; } = string.Empty;

    public List<OnlineCinemaEpisode> Episodes { get; } = new();
}


public class OnlineCinemaPlaylist
{
    // Films: a flat voiceover list; series: seasons → episodes → voiceovers
    public List<OnlineCinemaSeason>? Seasons { get; set; }

    public List<OnlineCinemaLeaf>? Voiceovers { get; set; }
}
