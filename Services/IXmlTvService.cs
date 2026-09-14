using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IptvPlayer.Models;

namespace IptvPlayer.Services;


public sealed class XmlTvLoadResult
{
    public List<EPGEntry> Entries { get; init; } = new();


    public Dictionary<string, string> ChannelIcons { get; init; } = new(StringComparer.OrdinalIgnoreCase);


    public DateTime DataSavedAtUtc { get; init; }
}

public interface IXmlTvService
{


    Task<XmlTvLoadResult> LoadAsync(EPGSource source, TimeSpan? maxAge = null, int daysBack = 3, CancellationToken ct = default);


    event Action<string, bool, string?>? SourceLoadFinished;


    Task<string?> ValidateEpgSourceAsync(string url, CancellationToken ct = default);
}
