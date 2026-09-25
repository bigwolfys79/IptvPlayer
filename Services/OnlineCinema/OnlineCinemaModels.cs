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

    // Voiceover/episode tracks of the embed playlist (first = default); each
    // carries its own master link and renditions
    public List<OnlineCinemaTrack> Tracks { get; } = new();
}


// One voiceover/episode track resolved from the cinemar playlist
public class OnlineCinemaTrack
{
    public string Label { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public Dictionary<string, string> Variants { get; } = new();
}
