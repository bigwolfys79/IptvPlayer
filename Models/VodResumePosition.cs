using System;

namespace IptvPlayer.Models;


public class VodResumePosition
{
    public double PositionSeconds { get; set; }


    public double DurationSeconds { get; set; }


    public int EpisodeIndex { get; set; } = -1;


    // Online cinema series: the season index within the episode tree
    public int OcSeasonIndex { get; set; } = -1;


    public DateTime UpdatedAt { get; set; } = DateTime.Now;


    public int? PortalPlaylistId { get; set; }
}
