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

        var utc = new DateTimeOffset(programStart).ToUnixTimeSeconds();
        var lutc = DateTimeOffset.Now.ToUnixTimeSeconds();

        var separator = liveUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{liveUrl}{separator}utc={utc}&lutc={lutc}";
    }
}
