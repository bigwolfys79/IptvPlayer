using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace IptvPlayer.Services;

// One track of the cinemar embed playlist: a voiceover/episode with its API payload
public class CinemarTrack
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Title2 { get; set; } = string.Empty;

    public string Data { get; set; } = string.Empty;
}


// Decoder for the obfuscated playlist ("#2..." format), a faithful
// port of the player JS: "#2" + delimiter code (2 digits) + parts split by the
// delimiter char, long parts rotated by their last digit, then base64 + UTF-8
// (the player does atob + escape + decodeURIComponent — same net effect).
// Verified against the site's own player.js on live data.
public static class CinemarDecoder
{
    private const int MaxShortPartLength = 32; // player: Math.pow(2, 5)

    public static List<CinemarTrack> DecodePlaylist(string encoded)
    {
        if (encoded == null || !encoded.StartsWith("#2", StringComparison.Ordinal))
        {
            throw new ArgumentException("Строка плейлиста не начинается с #2", nameof(encoded));
        }

        var body = encoded[2..];
        var delimiter = (char)int.Parse(body[..2]);
        var joined = new StringBuilder(body.Length);
        foreach (var part in body[2..].Split(delimiter))
        {
            joined.Append(SlicePart(part));
        }

        var base64 = joined.ToString();
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));

        using var doc = JsonDocument.Parse(json);
        var result = new List<CinemarTrack>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            result.Add(new CinemarTrack
            {
                Id = el.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                Title = el.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                Title2 = el.TryGetProperty("title2", out var title2) ? title2.GetString() ?? string.Empty : string.Empty,
                Data = el.TryGetProperty("data", out var data) ? data.GetString() ?? string.Empty : string.Empty
            });
        }

        return result;
    }

    // JS: e.length > 32 ? e.substr(2*t, e.length-3*t-1) + e.substr(0, t) : e,
    // where t is the last character's digit; a non-digit last char makes the
    // JS indices NaN and the part empty — mirrored here
    private static string SlicePart(string s)
    {
        if (s.Length == 0 || s.Length <= MaxShortPartLength)
        {
            return s;
        }

        var last = s[^1];
        if (!char.IsDigit(last))
        {
            return string.Empty;
        }

        var t = last - '0';
        var headLength = Math.Min(2 * t, s.Length);
        var take = Math.Max(0, Math.Min(s.Length - 3 * t - 1, s.Length - headLength));
        return s.Substring(headLength, take) + s.Substring(0, Math.Min(t, s.Length));
    }
}
