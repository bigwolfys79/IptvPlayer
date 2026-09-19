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

            // Sort by start time so overlap can be checked in O(log n)
            var sortedEntries = sourceResult.Entries.OrderBy(e => e.StartTime).ToList();

            foreach (var entry in sortedEntries)
            {
                if (!byChannel.TryGetValue(entry.ChannelId, out var list))
                {
                    list = new List<EPGEntry>();
                    byChannel[entry.ChannelId] = list;
                }

                if (!OverlapsAccepted(list, entry.StartTime, entry.EndTime))
                {
                    // Keep list sorted by start time for binary search
                    var pos = list.BinarySearch(entry, StartTimeComparer.Instance);
                    if (pos < 0)
                    {
                        pos = ~pos;
                    }
                    list.Insert(pos, entry);
                }
            }
        }

        logger.LogInformation(
            "Диагностика описаний: всего программ {Total}, с описанием {WithDesc}, пустых {Empty}.",
            byChannel.Values.Sum(l => l.Count),
            byChannel.Values.Sum(l => l.Count(e => !string.IsNullOrEmpty(e.Description))),
            byChannel.Values.Sum(l => l.Count(e => string.IsNullOrEmpty(e.Description))));

        return (byChannel, iconsByChannelId, BuildNameIndex(byChannel, logger));
    }


    private sealed class StartTimeComparer : IComparer<EPGEntry>
    {
        public static readonly StartTimeComparer Instance = new();

        public int Compare(EPGEntry? x, EPGEntry? y) => x!.StartTime.CompareTo(y!.StartTime);
    }


    // Accepted entries are pairwise non-overlapping and sorted by start time,
    // so only the earliest entry still open at 'start' can overlap the candidate
    private static bool OverlapsAccepted(List<EPGEntry> sorted, DateTime start, DateTime end)
    {
        int lo = 0, hi = sorted.Count - 1, idx = -1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (sorted[mid].EndTime > start)
            {
                idx = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return idx >= 0 && sorted[idx].StartTime < end;
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
