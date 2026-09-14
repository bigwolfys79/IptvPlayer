using System;

namespace IptvPlayer.Services;


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
