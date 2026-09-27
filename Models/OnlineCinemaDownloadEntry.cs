namespace IptvPlayer.Models;

using System.Collections.Generic;

// One row of the persisted downloads list (settings.json): unfinished rows
// resume after a restart, finished ones stay in the history to be opened
public class OnlineCinemaDownloadEntry
{
    public string Url { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public string Quality { get; set; } = string.Empty;

    // Stream-resolution context — enough to fetch a fresh CDN link when the
    // hour-bound token in Url expires
    public string Origin { get; set; } = string.Empty;

    public string LeafData { get; set; } = string.Empty;

    public string EmbedUrl { get; set; } = string.Empty;

    // Film page URL for HLS (nextembed) downloads — used to re-resolve
    public string PageUrl { get; set; } = string.Empty;

    // true → HLS remux download (nextembed stream), false → direct MP4
    public bool IsHls { get; set; }
    public string EpisodeKey { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }
}
