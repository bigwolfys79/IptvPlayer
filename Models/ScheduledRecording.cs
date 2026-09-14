using System;

namespace IptvPlayer.Models;


public class ScheduledRecording
{
    public string ChannelName { get; set; } = string.Empty;

    public string ProgramName { get; set; } = string.Empty;

    public DateTime StartTime { get; set; }


    public double DurationSec { get; set; }
}
