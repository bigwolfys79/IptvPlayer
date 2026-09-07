using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IptvPlayer.Models;

namespace IptvPlayer.Services;

/// <summary>
/// Результат загрузки одного XMLTV-источника: программы + логотипы каналов
/// из &lt;icon src&gt; (используются как резервный источник LogoUrl для
/// каналов без tvg-logo в плейлисте — см. EPGService.ApplyMissingLogosAsync).
/// </summary>
public sealed class XmlTvLoadResult
{
    public List<EPGEntry> Entries { get; init; } = new();

    /// <summary>channel id → icon url из &lt;icon src&gt; этого источника.</summary>
    public Dictionary<string, string> ChannelIcons { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Момент (UTC), когда данные этого источника были скачаны по сети —
    /// из SavedAtUtc дискового кэша либо «сейчас» для свежескачанного.
    /// Используется ключом кэша слитого EPG (см. EPGService): пока метки
    /// всех источников не изменились, слияние можно не повторять.
    /// </summary>
    public DateTime DataSavedAtUtc { get; init; }
}

public interface IXmlTvService
{
    /// <summary>
    /// Загружает и парсит один XMLTV-источник (с кэшированием по TTL внутри).
    /// EPGEntry.ChannelId здесь — это id канала из XMLTV (&lt;channel id="..."&gt;),
    /// которое затем сопоставляется с ChannelViewModel.TvgId.
    ///
    /// Раньше возвращал List&lt;EPGEntry&gt; без метаданных — расширено до XmlTvLoadResult,
    /// чтобы заодно прокинуть ChannelIcons (&lt;icon src&gt; из &lt;channel&gt;) без
    /// отдельного повторного прохода по тому же XML.
    ///
    /// maxAge — периодичность обновления EPG из настроек (1/3/7 дней):
    /// пока с момента последнего скачивания прошло меньше maxAge, источник
    /// берётся из дискового кэша без обращения к сети. TimeSpan.MaxValue —
    /// никогда не перекачивать автоматически. null — прежнее поведение с
    /// фиксированным 3-часовым TTL.
    ///
    /// daysBack — глубина архива из настроек (EpgArchiveDaysBack, 1/3/7):
    /// сколько дней назад парсить передачи (вперёд — фиксированные DaysAhead
    /// дней). Входит в ключ дискового кэша: при смене настройки источник
    /// перекачивается, старый кэш остаётся для отката.
    /// </summary>
    Task<XmlTvLoadResult> LoadAsync(EPGSource source, TimeSpan? maxAge = null, int daysBack = 3, CancellationToken ct = default);
}
