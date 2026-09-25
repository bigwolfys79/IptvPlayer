using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace IptvPlayer.Services;

// One node of the embed playlist tree: films — a flat list of
// voiceover leaves (Data → api payload); series — nested seasons → episodes
// → voiceovers via Folder
public class CinemarNode
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Title2 { get; set; } = string.Empty;

    public string Data { get; set; } = string.Empty;

    public List<CinemarNode>? Folder { get; set; }
}


// Decoder for the obfuscated embed playlist ("#2..." format), a faithful
// port of the player JS: "#2" + delimiter code (2 digits) + parts split by the
// delimiter char, long parts rotated by their last digit, then base64 + UTF-8
// (the player does atob + escape + decodeURIComponent — same net effect).
// Verified against the site's own player.js on live data.
public static class CinemarDecoder
{
    private const int MaxShortPartLength = 32; // player: Math.pow(2, 5)

    public static List<CinemarNode> DecodePlaylist(string encoded)
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
        return ParseNodes(doc.RootElement);
    }

    // Flattens the playlist tree into playable leaves: films — voiceover leaves,
    // series — one entry per episode (first voiceover). Title2 ("1 сезон 1
    // серия") carries the season/episode position, so entries are deduped by it
    public static List<(string Label, string Data)> FlattenLeaves(List<CinemarNode> nodes)
    {
        var result = new List<(string Label, string Data)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(nodes, string.Empty, result, seen);
        return result;
    }

    private static void Walk(List<CinemarNode> nodes, string prefix,
        List<(string Label, string Data)> result, HashSet<string> seen)
    {
        foreach (var node in nodes)
        {
            if (node.Folder is { Count: > 0 })
            {
                Walk(node.Folder, string.IsNullOrEmpty(prefix) ? node.Title : $"{prefix} · {node.Title}", result, seen);
                continue;
            }

            if (string.IsNullOrWhiteSpace(node.Data))
            {
                continue;
            }

            // Series: the leaf's Title2 ("1 сезон 1 серия") is the episode
            // position — keep the first voiceover, drop the rest of that
            // episode. Position is already in the prefix, so the label is
            // prefix + voiceover name
            var key = node.Title2.Length > 0 ? node.Title2 : prefix + node.Title;
            if (!seen.Add(key))
            {
                continue;
            }

            var label = string.IsNullOrEmpty(prefix)
                ? (node.Title2.Length > 0 ? node.Title2 : node.Title)
                : $"{prefix} · {node.Title}";
            result.Add((label, node.Data));
        }
    }

    private static List<CinemarNode> ParseNodes(JsonElement array)
    {
        var result = new List<CinemarNode>();
        foreach (var el in array.EnumerateArray())
        {
            result.Add(new CinemarNode
            {
                Id = el.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                Title = el.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                Title2 = el.TryGetProperty("title2", out var title2) ? title2.GetString() ?? string.Empty : string.Empty,
                Data = el.TryGetProperty("data", out var data) ? data.GetString() ?? string.Empty : string.Empty,
                Folder = el.TryGetProperty("folder", out var folder) && folder.ValueKind == JsonValueKind.Array
                    ? ParseNodes(folder)
                    : null
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
