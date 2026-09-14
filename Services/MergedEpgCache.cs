using System;
using System.Collections.Generic;
using MemoryPack;

namespace IptvPlayer.Services;


[MemoryPackable]
public sealed partial class MergedEpgCache
{
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public const int CurrentFormatVersion = 1;


    public List<string> SourceUrls { get; set; } = new();


    public List<DateTime> SourceSavedAtUtc { get; set; } = new();

    public Dictionary<string, List<Models.EPGEntry>> ByChannel { get; set; } = new();

    public Dictionary<string, string> IconsByChannelId { get; set; } = new();
}
