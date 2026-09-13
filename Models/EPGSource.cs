using System;

namespace IptvPlayer.Models;

/// <summary>
/// Один источник XMLTV-данных. Список таких источников хранится в
/// <see cref="AppSettings"/> и персистится через <c>ISettingsService</c>.
/// Порядок в списке важен: при слиянии программ из нескольких источников
/// приоритет отдаётся более раннему по порядку источнику (см. EPGService).
/// </summary>
public class EPGSource
{
    public string Url { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Текст последней ошибки загрузки источника (сеть, не XMLTV и т.п.).
    /// Null — после последней попытки ошибок не было.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Момент последней успешной загрузки и разбора источника. Null —
    /// источник ещё ни разу не загружался успешно.
    /// </summary>
    public DateTimeOffset? LastSuccessAt { get; set; }
}
