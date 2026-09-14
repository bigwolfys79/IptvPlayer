using System;
using System.Collections.Generic;
using MemoryPack;

namespace IptvPlayer.Services;


[MemoryPackable]
public sealed partial class CachedXmlTv
{


    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public const int CurrentFormatVersion = 2;

    public List<Models.EPGEntry> Entries { get; set; } = new();

    public Dictionary<string, string> ChannelIcons { get; set; } = new(StringComparer.OrdinalIgnoreCase);


    public DateTime SavedAtUtc { get; set; }

    public DateTime ExpiresAt { get; set; }
}
