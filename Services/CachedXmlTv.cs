using System;
using System.Collections.Generic;
using MemoryPack;

namespace IptvPlayer.Services;

/// <summary>
/// Обёртка распарсенного XMLTV-источника для дискового кэша (см. XmlTvService
/// и EpgCacheStore). Раньше была приватным классом внутри XmlTvService и
/// сериализовалась в JSON через CacheService — вынесена наружу и переведена
/// на MemoryPack: чтение кэша из ~секунд (System.Text.Json на сотнях МБ)
/// падает до долей секунды.
/// </summary>
[MemoryPackable]
public sealed partial class CachedXmlTv
{
    /// <summary>
    /// Версия бинарного формата кэша. При любом изменении сериализуемых
    /// полей (EPGEntry/CachedXmlTv) увеличивается — старый файл перестаёт
    /// читаться и воспринимается как промах кэша (источник перекачается).
    /// </summary>
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public const int CurrentFormatVersion = 1;

    public List<Models.EPGEntry> Entries { get; set; } = new();

    public Dictionary<string, string> ChannelIcons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Когда запись реально сохранена (UTC) — от неё считается возраст
    /// кэша при периодичности обновления из настроек (maxAge).
    /// У записей старого формата равно default — см. GetSavedAtUtc.
    /// </summary>
    public DateTime SavedAtUtc { get; set; }

    public DateTime ExpiresAt { get; set; }
}
