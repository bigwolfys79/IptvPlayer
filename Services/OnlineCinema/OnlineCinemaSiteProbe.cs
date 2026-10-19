using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;

// Probes a custom online-cinema site at playlist-add time: downloads the home
// page, detects the DLE engine and template, discovers categories from the
// site menu and the search kind. Produces a persisted site profile
public static class OnlineCinemaSiteProbe
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Result of a probe: a ready profile or a user-facing reason
    public record ProbeResult(OnlineCinemaSiteProfile? Profile, string? Error);

    public static async Task<OnlineCinemaSiteProfile?> ProbeAsync(
        string url, CancellationToken ct = default, ILogger? logger = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(baseUri.Host))
        {
            return null;
        }

        var baseUrl = baseUrlOf(url);
        var html = await FetchAsync(baseUrl + "/", baseUrl, ct);
        if (html == null)
        {
            return null;
        }

        var isDle = html.Contains("/engine/classes/js/dle_js.js", StringComparison.OrdinalIgnoreCase) ||
                    html.Contains("/engine/ajax/", StringComparison.OrdinalIgnoreCase) ||
                    html.Contains("/templates/", StringComparison.OrdinalIgnoreCase);
        if (!isDle)
        {
            logger?.LogInformation("Онлайн-кинотеатр: {Host} не похож на DLE-сайт.", baseUrl);
            return null;
        }

        var template = html.Contains("article", StringComparison.OrdinalIgnoreCase) &&
                       html.Contains("shortstory", StringComparison.OrdinalIgnoreCase)
            ? "shortstory"
            : html.Contains("card__title", StringComparison.OrdinalIgnoreCase)
                ? "card"
                : null;
        if (template == null)
        {
            logger?.LogInformation("Онлайн-кинотеатр: {Host} — шаблон карточек не распознан.", baseUrl);
            return null;
        }

        var categories = await DiscoverCategoriesAsync(baseUrl, html, ct);
        if (categories.Count == 0)
        {
            return null;
        }

        // Year listing path: prefer /xfsearch/god/{0}/, fall back to /filmy/{0}/
        var yearPattern = html.Contains("/xfsearch/god/", StringComparison.OrdinalIgnoreCase)
            ? "/xfsearch/god/{0}/"
            : html.Contains("/filmy/202", StringComparison.OrdinalIgnoreCase) ? "/filmy/{0}/" : string.Empty;

        // Search kind: lightsearch AJAX first, plain DLE search second
        string searchKind;
        if (html.Contains("lightsearch", StringComparison.OrdinalIgnoreCase))
        {
            searchKind = "lightsearch";
        }
        else
        {
            var searchPage = await FetchAsync(
                baseUrl + "/index.php?do=search&subaction=search&story=тест",
                baseUrl, ct);
            searchKind = searchPage != null && !LooksLikeChallenge(searchPage) &&
                         searchPage.Contains("dle-content", StringComparison.OrdinalIgnoreCase)
                ? "dlesearch"
                : "none";
        }

        return new OnlineCinemaSiteProfile
        {
            BaseUrl = baseUrl,
            Template = template,
            Categories = categories,
            YearPathPattern = yearPattern,
            SearchKind = searchKind,
            ChallengeProtected = false
        };
    }

    // Builds the category map from the site nav: menu links whose text matches
    // the known-genre whitelist, plus the base films/series listings
    private static async Task<Dictionary<string, string>> DiscoverCategoriesAsync(
        string baseUrl, string html, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var host = new Uri(baseUrl).Host;

        foreach (var m in Regex.Matches(html, @"<a href=""([^""]+/)""[^>]*>([^<]{2,30})</a>")
                    .Cast<Match>())
        {
            var raw = m.Groups[1].Value;
            var label = m.Groups[2].Value.Trim().ToLowerInvariant();
            var full = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? raw
                : baseUrl.TrimEnd('/') + raw;
            if (!Uri.TryCreate(full, UriKind.Absolute, out var uri) ||
                !uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A category link is a short site path ending in /
            var path = uri.AbsolutePath;
            if (path.Count(c => c == '/') > 2 || path.EndsWith("page/1/"))
            {
                continue;
            }

            var name = label.ToLowerInvariant();
            var known = M3UParserService.KnownGenres.Contains(name) ? name
                : GenreAliases.TryGetValue(name, out var alias) ? alias : null;
            if (known == null)
            {
                continue;
            }

            map.TryAdd(known, path);
            ct.ThrowIfCancellationRequested();
        }

        // Base listings must exist — otherwise the page is not a catalog
        var filmsPath = await FirstExistingPathAsync(baseUrl, host, new[] { "/filmy/", "/films/", "/movie/" }, ct);
        if (filmsPath == null)
        {
            return map;
        }

        map.TryAdd("все фильмы", filmsPath);
        var seriesPath = await FirstExistingPathAsync(baseUrl, host, new[] { "/serialy/", "/series/" }, ct);
        if (seriesPath != null)
        {
            map["сериалы"] = seriesPath;
        }

        return map;
    }

    private static readonly Dictionary<string, string> GenreAliases = new()
    {
        ["драмы"] = "драма",
        ["комедии"] = "комедия",
        ["триллеры"] = "триллер",
        ["ужасы"] = "ужасы",
        ["мелодрамы"] = "мелодрама",
        ["детективы"] = "детектив",
        ["боевики"] = "боевик",
        ["военные"] = "военный",
        ["мультфильмы"] = "мультфильмы",
        ["документальные"] = "документальные",
        ["исторические"] = "исторические",
        ["вестерны"] = "вестерн",
        ["мюзиклы"] = "мюзикл",
        ["приключения"] = "приключения",
        ["криминал"] = "криминал",
        ["семейные"] = "семейный",
        ["фантастика"] = "фантастика",
        ["фэнтези"] = "фэнтези",
    };

    // Minimal probes for the base listings
    private static async Task<string?> FirstExistingPathAsync(
        string baseUrl, string host, string[] candidates, CancellationToken ct)
    {
        foreach (var path in candidates)
        {
            var html = await FetchAsync(baseUrl + path, baseUrl, ct);
            if (html != null && !LooksLikeChallenge(html) &&
                (html.Contains("shortstory", StringComparison.OrdinalIgnoreCase) ||
                 html.Contains("card__title", StringComparison.OrdinalIgnoreCase)))
            {
                return path;
            }
        }

        return null;
    }

    private static bool LooksLikeChallenge(string html) =>
        html.Contains("just a moment", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("cf-chl", StringComparison.OrdinalIgnoreCase);

    private static string baseUrlOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? u.GetLeftPart(System.UriPartial.Authority)
            : url;

    private static async Task<string?> FetchAsync(string url, string referer, CancellationToken ct)
    {
        if (OnlineCinemaMethods.UseCurl && await CurlHttp.GetAvailabilityAsync())
        {
            var result = await CurlHttp.GetAsync(url, referer, iframe: false, ct);
            if (result is { Status: 200 } && !LooksLikeChallenge(result.Body))
            {
                return result.Body;
            }

            return null;
        }

        if (!OnlineCinemaMethods.UseHttpClient)
        {
            return null;
        }

        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, url);
            KinogoSite.ApplyBrowserHeaders(msg);
            using var resp = await Client.SendAsync(msg, ct);
            var body = resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
            return body != null && !LooksLikeChallenge(body) ? body : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
