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
}
