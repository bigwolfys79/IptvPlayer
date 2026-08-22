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
}

public interface IXmlTvService
{
    /// <summary>
    /// Загружает и парсит один XMLTV-источник (с кэшированием по TTL внутри).
    /// EPGEntry.ChannelId здесь — это id канала из XMLTV (&lt;channel id="..."&gt;),
    /// которое затем сопоставляется с ChannelViewModel.TvgId.
    ///
    /// Раньше возвращал голый List&lt;EPGEntry&gt; — расширено до XmlTvLoadResult,
    /// чтобы заодно прокинуть ChannelIcons (&lt;icon src&gt; из &lt;channel&gt;) без
    /// отдельного повторного прохода по тому же XML.
    ///
    /// maxAge — периодичность обновления EPG из настроек (1/3/7 дней):
    /// пока с момента последнего скачивания прошло меньше maxAge, источник
    /// берётся из дискового кэша без обращения к сети. TimeSpan.MaxValue —
    /// никогда не перекачивать автоматически. null — прежнее поведение с
    /// фиксированным 3-часовым TTL.
    /// </summary>
    Task<XmlTvLoadResult> LoadAsync(EPGSource source, TimeSpan? maxAge = null, CancellationToken ct = default);
}
