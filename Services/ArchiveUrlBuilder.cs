using System;

namespace IptvPlayer.Services;

/// <summary>
/// Строит URL архивного (timeshift) потока для провайдеров, которые принимают
/// на live-URL два query-параметра: utc — epoch-секунды точки, с которой
/// начинать показ, и lutc — epoch-секунды текущего момента. Дальше провайдер
/// сам отдаёт сдвинутый вперёд плейлист с валидными подписями сегментов
/// (md5), клиенту ничего пересчитывать не нужно.
/// </summary>
public static class ArchiveUrlBuilder
{
    public static string BuildUrl(string liveUrl, DateTime programStart)
    {
        // Времена EPG хранятся как локальные (XmlTvService приводит их к
        // ToLocalTime()), поэтому DateTimeOffset(DateTime) подставляет
        // текущее смещение зоны и даёт корректные epoch-секунды.
        var utc = new DateTimeOffset(programStart).ToUnixTimeSeconds();
        var lutc = DateTimeOffset.Now.ToUnixTimeSeconds();

        // Live-URL часто уже содержит свои query-параметры (токен и т.п.) —
        // новые добавляем тем же списком, а не вторым '?'.
        var separator = liveUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{liveUrl}{separator}utc={utc}&lutc={lutc}";
    }
}
