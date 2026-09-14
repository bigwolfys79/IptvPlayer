using System;
using System.Collections.Generic;

namespace IptvPlayer.Models;


public class PlaylistCache
{
    public const int CurrentFormatVersion = 5;

    public int FormatVersion { get; set; }


    public DateTime SavedAtUtc { get; set; }


    public string? PortalKeyHash { get; set; }

    public List<CachedChannel> Channels { get; set; } = new();
}


public class CachedChannel
{
    public string Name { get; set; } = string.Empty;
    public string? StreamUrl { get; set; }
    public string? LogoUrl { get; set; }
    public string? Group { get; set; }
    public string? TvgId { get; set; }


    public int CatchupDays { get; set; }


    public string? PortalRequest { get; set; }


    public string? Description { get; set; }

    public int Year { get; set; }


    public string? Genre { get; set; }
}
