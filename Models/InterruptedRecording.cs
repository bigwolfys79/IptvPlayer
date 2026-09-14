using System;

namespace IptvPlayer.Models;


public class InterruptedRecording
{
    public string ChannelName { get; set; } = string.Empty;
    public string ProgramName { get; set; } = string.Empty;


    public DateTime? EndTime { get; set; }
}
