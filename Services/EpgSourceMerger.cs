using System;
using System.Collections.Generic;
using System.Linq;
using IptvPlayer.Models;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;


public static class EpgSourceMerger
{


    public static (Dictionary<string, List<EPGEntry>> ByChannel,
                   Dictionary<string, string> IconsByChannelId,
                   Dictionary<string, List<EPGEntry>> NameIndex) Merge(
        List<XmlTvLoadResult> sourceResults,
        ILogger logger)
    {
        var byChannel = new Dictionary<string, List<EPGEntry>>(StringComparer.OrdinalIgnoreCase);
        var iconsByChannelId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceResult in sourceResults)
        {

            foreach (var (channelId, iconUrl) in sourceResult.ChannelIcons)
            {
                iconsByChannelId.TryAdd(channelId, iconUrl);
            }

            foreach (var entry in sourceResult.Entries)
            {
                if (!byChannel.TryGetValue(entry.ChannelId, out var list))
                {
                    list = new List<EPGEntry>();
                    byChannel[entry.ChannelId] = list;
                }

                var overlapsExisting = list.Any(existing =>
                    entry.StartTime < existing.EndTime && existing.StartTime < entry.EndTime);

                if (!overlapsExisting)
                {
                    list.Add(entry);
                }
            }
        }

        foreach (var list in byChannel.Values)
        {
            list.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
        }

        logger.LogInformation(
            "Диагностика описаний: всего программ {Total}, с описанием {WithDesc}, пустых {Empty}.",
            byChannel.Values.Sum(l => l.Count),
            byChannel.Values.Sum(l => l.Count(e => !string.IsNullOrEmpty(e.Description))),
            byChannel.Values.Sum(l => l.Count(e => string.IsNullOrEmpty(e.Description))));

        return (byChannel, iconsByChannelId, BuildNameIndex(byChannel, logger));
    }


    public static Dictionary<string, List<EPGEntry>> BuildNameIndex(
        Dictionary<string, List<EPGEntry>> byChannel,
        ILogger logger)
    {
        var groups = new Dictionary<string, List<(string ChannelId, string RawName, List<EPGEntry> Entries)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, entries) in byChannel)
        {
            if (entries.Count == 0)
            {
                continue;
            }

            var rawName = entries[0].ChannelName ?? string.Empty;
            var normalized = EpgNameNormalizer.Normalize(rawName);
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            if (!groups.TryGetValue(normalized, out var list))
            {
                list = new List<(string, string, List<EPGEntry>)>();
                groups[normalized] = list;
            }

            list.Add((id, rawName, entries));
        }

        var result = new Dictionary<string, List<EPGEntry>>(StringComparer.OrdinalIgnoreCase);

        var qualityDupCount = 0;
        var ambiguousNames = new List<string>();

        foreach (var (normalized, group) in groups)
        {
            if (group.Count == 1)
            {
                result[normalized] = group[0].Entries;
                continue;
            }

            var keepQualifiersKeys = group
                .Select(g => EpgNameNormalizer.NormalizeKeepQualifiers(g.RawName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (keepQualifiersKeys.Count == 1)
            {

                var chosen = group
                    .OrderByDescending(g => g.Entries.Count)
                    .ThenByDescending(g => EpgNameNormalizer.GetQualityRank(g.RawName))
                    .First();

                qualityDupCount++;
                result[normalized] = chosen.Entries;
            }
            else
            {

                var chosen = group
                    .OrderByDescending(g => g.Entries.Count)
                    .ThenByDescending(g => EpgNameNormalizer.GetQualityRank(g.RawName))
                    .First();

                ambiguousNames.Add(normalized);
                result[normalized] = chosen.Entries;
            }
        }

        if (qualityDupCount > 0)
        {
            logger.LogInformation(
                "Индекс имён: у {Count} каналов в XMLTV несколько id, различающихся только " +
                "качеством (HD/SD/4K и т.п.) — для каждого выбран один id (максимум программ, затем " +
                "максимальное качество), остальные пропущены как дубли.",
                qualityDupCount);
        }

        if (ambiguousNames.Count > 0)
        {


            ambiguousNames.Sort(StringComparer.OrdinalIgnoreCase);
            logger.LogWarning(
                "Индекс имён: {Count} нормализованных имён соответствуют нескольким разным " +
                "id в XMLTV (различие не только в качестве) — для них выбран лучший кандидат эвристически " +
                "(расписание может оказаться соседнего региона): {Names}. " +
                "Точное сопоставление для этих каналов даст корректный tvg-id в плейлисте.",
                ambiguousNames.Count, string.Join(", ", ambiguousNames.Select(n => $"\"{n}\"")));
        }

        var brandAliasAdds = new List<(string AltKey, List<EPGEntry> Entries)>();
        foreach (var (key, entries) in result)
        {
            if (!key.StartsWith("tviksel ", StringComparison.Ordinal))
            {
                continue;
            }

            var altKey = key["tviksel ".Length..];
            if (altKey.Length > 0 && !result.ContainsKey(altKey))
            {
                brandAliasAdds.Add((altKey, entries));
            }
        }
        foreach (var (altKey, entries) in brandAliasAdds)
        {
            result[altKey] = entries;
        }

        return result;
    }
}
