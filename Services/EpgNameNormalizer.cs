using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace IptvPlayer.Services;


public static class EpgNameNormalizer
{

    private static readonly Regex NoiseTokenRegex =
        new(@"\b(hd|fhd|uhd|sd|4k|hevc|full\s*hd)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ParenthesesRegex = new(@"\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex NonAlphaNumericRegex = new(@"[^\p{L}\p{Nd}\s]", RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex TrailingCountryCodeRegex = new(@"\.[a-zа-я]{2,3}$", RegexOptions.Compiled);

    private static readonly Regex BarePlusRegex = new(@"\+(?!\d)", RegexOptions.Compiled);

    private static readonly Regex TrailingTimeshiftRegex = new(@"\+\s*\d{1,2}\s*$", RegexOptions.Compiled);

    private static readonly Regex KinozalAliasRegex = new(@"\bкинозал\b", RegexOptions.Compiled);

    private static readonly HashSet<string> TrailingCountryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "uk", "us", "fr", "de", "it", "es", "pl", "br", "cn", "jp", "kr", "in", "tr", "ua", "by",
        "kz", "az", "ge", "am", "lt", "lv", "ee", "rs", "hu", "ro", "bg", "gr", "nl", "se", "no",
        "dk", "fi", "at", "ch", "be", "pt", "ie", "cz", "sk", "si", "hr", "md", "il", "ae", "sa",
        "eg", "za", "ng", "th", "vn", "id", "my", "sg", "au", "nz", "ca", "mx", "ar", "cl", "co",
        "pe", "eu", "intl", "international", "международный",
    };

    private static readonly HashSet<string> TrailingStreamMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "orig", "50", "60", "hdr", "1080p", "1080i", "720p", "2160p", "50p", "60p",
    };

    private static readonly Dictionary<string, int> QualityRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sd"] = 1,
        ["hevc"] = 1,
        ["hd"] = 2,
        ["fhd"] = 3,
        ["full hd"] = 3,
        ["4k"] = 4,
        ["uhd"] = 4,
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new();


    internal static string Normalize(string? name)
        => NormalizeCached('n', name, false, false);


    internal static string NormalizePreservingTimeshift(string? name)
        => NormalizeCached('t', name, false, true);


    internal static string NormalizeKeepQualifiers(string? name)
        => NormalizeCached('q', name, true, false);

    private static string NormalizeCached(char variant, string? name, bool keepQualifiers, bool keepTimeshift)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return Cache.GetOrAdd(
            string.Concat(variant, ':', name),
            static (_, arg) => NormalizeCore(arg.Name, arg.KeepQualifiers, arg.KeepTimeshift),
            (Variant: variant, Name: name, KeepQualifiers: keepQualifiers, KeepTimeshift: keepTimeshift));
    }

    private static string NormalizeCore(string? name, bool keepQualifiers, bool keepTimeshift)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var s = name.Trim().ToLowerInvariant().Replace('ё', 'е');
        s = TrailingCountryCodeRegex.Replace(s, string.Empty);
        if (!keepQualifiers)
        {


            s = ParenthesesRegex.Replace(s, " ");
        }
        s = NoiseTokenRegex.Replace(s, " ");
        s = s.Replace("+0", " ");
        if (!keepTimeshift)
        {
            s = TrailingTimeshiftRegex.Replace(s, " ");
        }
        s = BarePlusRegex.Replace(s, " plus ");
        if (keepQualifiers)
        {
            s = s.Replace("(", " ").Replace(")", " ");
        }
        s = NonAlphaNumericRegex.Replace(s, " ");
        s = MultiSpaceRegex.Replace(s, " ").Trim();
        s = KinozalAliasRegex.Replace(s, "кино");

        return StripTrailingMarkers(s);
    }


    private static string StripTrailingMarkers(string normalized)
    {
        if (string.IsNullOrEmpty(normalized))
        {
            return normalized;
        }

        var tokens = normalized.Split(' ');
        var keep = tokens.Length;
        while (keep > 1)
        {
            var last = tokens[keep - 1];
            if (!TrailingCountryCodes.Contains(last) && !TrailingStreamMarkers.Contains(last))
            {
                break;
            }
            keep--;
        }

        if (keep == tokens.Length)
        {
            return normalized;
        }

        return string.Join(" ", tokens, 0, keep);
    }


    internal static int GetQualityRank(string rawName)
    {
        var best = 0;
        foreach (Match m in NoiseTokenRegex.Matches(rawName))
        {
            var token = MultiSpaceRegex.Replace(m.Value.ToLowerInvariant(), " ").Trim();
            if (QualityRank.TryGetValue(token, out var rank))
            {
                best = Math.Max(best, rank);
            }
        }
        return best;
    }
}
