using System;

namespace IptvPlayer.Models;


public class VodResumePosition
{
    public double PositionSeconds { get; set; }


    public double DurationSeconds { get; set; }


    public int EpisodeIndex { get; set; } = -1;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;


    public int? PortalPlaylistId { get; set; }
}
