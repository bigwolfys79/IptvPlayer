using System;

namespace IptvPlayer.Services;


public static class ArchiveUrlBuilder
{
    public static string BuildUrl(string liveUrl, DateTime programStart)
    {

        var utc = new DateTimeOffset(programStart).ToUnixTimeSeconds();
        var lutc = DateTimeOffset.Now.ToUnixTimeSeconds();

        // Drop fragment — query params after # would land in the fragment
        var hashIndex = liveUrl.IndexOf('#');
        var baseUrl = hashIndex >= 0 ? liveUrl[..hashIndex] : liveUrl;

        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{baseUrl}{separator}utc={utc}&lutc={lutc}";
    }
}
