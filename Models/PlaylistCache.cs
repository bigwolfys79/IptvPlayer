using System;
using System.Collections.Generic;

namespace IptvPlayer.Models;

/// <summary>
/// Дисковый кэш разобранного плейлиста: чтобы при запуске не перекачивать
/// M3U, пока не истекла периодичность обновления из настроек
/// (AppSettings.PlaylistRefreshDays). Хранится отдельным файлом через
/// PlaylistCacheService — намеренно НЕ через CacheService: тот живёт в
/// подпапке cache и стирается целиком при "Обновить EPG", а кэш плейлиста
/// переживать это должен.
/// </summary>
public class PlaylistCache
{
    /// <summary>
    /// Версия формата кэша. При добавлении новых полей (CatchupDays) или
    /// смене их смысла — увеличивается: кэш старой версии считается
    /// устаревшим и плейлист перекачивается один раз при первом запуске
    /// обновлённого приложения (см. InitializeAsync).
    /// </summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; }

    /// <summary>Момент последнего успешного скачивания плейлиста (UTC).</summary>
    public DateTime SavedAtUtc { get; set; }

    public List<CachedChannel> Channels { get; set; } = new();
}

/// <summary>
/// Минимальный набор полей ChannelViewModel, необходимый для восстановления
/// списка каналов из кэша. Id намеренно не хранится — он назначается заново
/// при каждой загрузке (см. MainPage.InitializeAsync).
/// </summary>
public class CachedChannel
{
    public string Name { get; set; } = string.Empty;
    public string? StreamUrl { get; set; }
    public string? LogoUrl { get; set; }
    public string? Group { get; set; }
    public string? TvgId { get; set; }

    /// <summary>
    /// Глубина архива передач канала в днях из атрибута tvg-rec плейлиста
    /// (0 — архива нет). От него зависит зелёный маркер в списке каналов.
    /// </summary>
    public int CatchupDays { get; set; }
}
