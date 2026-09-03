using System;
using System.Collections.Generic;
using MemoryPack;

namespace IptvPlayer.Services;

/// <summary>
/// Кэш результата слияния XMLTV-источников (EpgSourceMerger.Merge):
/// индекс программ по tvg-id + индекс логотипов. Пока набор источников
/// (URL в порядке приоритета) и момент скачивания каждого не изменились,
/// слияние можно не повторять — на сотнях тысяч программ оно стоит
/// секунды CPU при каждом запуске, а десериализация этой записи вдвое
/// дешевле: читаем один файл вместо кэшей всех источников + пропускаем
/// проверку пересечений и сортировку.
///
/// Индекс по нормализованным именам НЕ хранится: он детерминированно
/// строится из ByChannel за миллисекунды (EpgSourceMerger.BuildNameIndex)
/// и экономить нечего.
///
/// Свежесть проверяется не по TTL файла, а по меткам скачивания источников
/// (SourceSavedAtUtc): при превышении периодичности обновления из настроек
/// кэш инвалидируется и следующий запуск уходит в полный путь — источник
/// перекачается, слияние выполнится заново, запишется новая запись.
/// Кнопка «Обновить EPG» чистит кэш целиком (EpgCacheStore.ClearAll).
/// </summary>
[MemoryPackable]
public sealed partial class MergedEpgCache
{
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// URL источников в порядке приоритета слияния (как в настройках).
    /// Изменение набора или порядка — другой кэш.
    /// </summary>
    public List<string> SourceUrls { get; set; } = new();

    /// <summary>
    /// Момент скачивания (UTC) каждого источника — параллельно SourceUrls.
    /// </summary>
    public List<DateTime> SourceSavedAtUtc { get; set; } = new();

    public Dictionary<string, List<Models.EPGEntry>> ByChannel { get; set; } = new();

    public Dictionary<string, string> IconsByChannelId { get; set; } = new();
}
