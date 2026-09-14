using System;

namespace IptvPlayer.Models;


public class ProgramReminder
{
    public int ChannelId { get; set; }

    public string ChannelName { get; set; } = string.Empty;

    public string ProgramName { get; set; } = string.Empty;

    public DateTime StartTime { get; set; }


    public bool Notified { get; set; }
}
