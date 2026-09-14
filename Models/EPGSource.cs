using System;

namespace IptvPlayer.Models;


public class EPGSource
{
    public string Url { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;


    public string? LastError { get; set; }


    public DateTimeOffset? LastSuccessAt { get; set; }
}
