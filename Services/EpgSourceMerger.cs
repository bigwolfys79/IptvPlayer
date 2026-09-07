using System;
using System.Collections.Generic;
using System.Linq;
using IptvPlayer.Models;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;

/// <summary>
/// Слияние нескольких XMLTV-источников в индексы для сопоставления с
/// каналами плейлиста: индекс программ по tvg-id, индекс логотипов и
/// индекс по нормализованным именам (резервный путь для каналов без
/// tvg-id). Чистая CPU-работа без await — вызывается только из пула
/// потоков (см. EPGService.DoEnsureEpgLoadedAsync), потому что на сотнях
/// тысяч программ занимает заметное время. Вынесено из EPGService.
/// </summary>
public static class EpgSourceMerger
{
    /// <summary>
    /// Сливает программы всех источников в один индекс по tvg-id и строит
    /// индекс по нормализованным именам. Источники обрабатываются в порядке
    /// списка настроек: первый имеет приоритет при пересечении программ
    /// по времени для одного канала, и его иконка выигрывает TryAdd.
    /// </summary>
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

    /// <summary>
    /// Строит индекс "нормализованное имя -> программы" из уже собранного
    /// по id индекса. Если два РАЗНЫХ id в XMLTV нормализуются в одно и то
    /// же имя (например "Первый канал" и "Первый канал (Москва)" после
    /// удаления скобок), сопоставление по имени неоднозначно — но прежде
    /// чем исключать такое имя целиком, проверяем: не различаются ли
    /// варианты ТОЛЬКО суффиксом качества (HD/FHD/4K/UHD/SD/HEVC), как
    /// "BCU Kids 4K" и "BCU Kids" — это один и тот же канал в разных
    /// потоках, и расписание передач у них практически всегда совпадает.
    /// Для этого сравниваем имена, из которых убран только суффикс
    /// качества, но НЕ убраны скобки (EpgNameNormalizer.NormalizeKeepQualifiers) —
    /// если различие ещё и в скобках (например "Первый городской (Одесса)"
    /// vs "(Омск)"), это настоящая неоднозначность: расписание может быть
    /// любым из кандидатов. Раньше такие имена исключались из индекса
    /// целиком (ни один канал с этим названием не получал EPG) — теперь
    /// выбирается детерминированно лучший кандидат, а имя попадает в
    /// список эвристических в сводке лога.
    ///
    /// Публичный для EPGService: кэш слитого EPG (MergedEpgCache) хранит
    /// только ByChannel, а этот индекс перестраивает после чтения — он
    /// детерминирован по ByChannel и стоит миллисекунды.
    /// </summary>
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
