using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace IptvPlayer.Services;

// Site profile captured from a live DLE site: selectors, category map,
// year/search endpoints. Persisted in settings (keyed by host) so a new
// site needs no code — the probe writes the profile on first add
public class OnlineCinemaSiteProfile
{
    public string BaseUrl { get; set; } = string.Empty;

    // "shortstory" (article.shortStory) or "card" (div.card d-flex)
    public string Template { get; set; } = "shortstory";

    // category name -> site path
    public Dictionary<string, string> Categories { get; set; } = new();

    // Path of the year listing with a {0} placeholder ("/xfsearch/god/{0}/"),
    // empty when the site has no year listing
    public string YearPathPattern { get; set; } = string.Empty;

    // "lightsearch" (POST q=...), "dlesearch" (GET story=...) or "none"
    public string SearchKind { get; set; } = "none";

    // True when the site blocks plain HTTP with a Cloudflare challenge —
    // HTTP tier is skipped for it, the hidden browser goes first
    public bool ChallengeProtected { get; set; }
}


// Generic DLE adapter: parses any DLE template (old shortstory / new card)
// from a live-captured profile. Created dynamically for custom playlist URLs
public sealed class GenericDleSite : IOnlineCinemaSite
{
    private readonly OnlineCinemaSiteProfile _profile;

    public GenericDleSite(OnlineCinemaSiteProfile profile)
    {
        _profile = profile;
        Host = Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var u) ? u.Host : string.Empty;
    }

    public string Host { get; }

    public string Id => "dle:" + Host;

    public string BaseUrl => _profile.BaseUrl.TrimEnd('/');

    public string DisplayName => Host;

    public bool SupportsSearch => _profile.SearchKind != "none";

    public IReadOnlyDictionary<string, string> Categories => _profile.Categories;

    public string CategoryUrl(string category, int page)
    {
        var path = _profile.Categories.TryGetValue(category, out var p) ? p : "/filmy/";
        return page <= 1 ? BaseUrl + path : $"{BaseUrl}{path}page/{page}/";
    }

    public string YearUrl(int year, int page = 1)
    {
        if (string.IsNullOrEmpty(_profile.YearPathPattern))
        {
            return CategoryUrl("все фильмы", page);
        }

        var path = _profile.YearPathPattern.Replace("{0}", year.ToString());
        return page <= 1 ? BaseUrl + path : $"{BaseUrl}{path}page/{page}/";
    }

    public string UserAgent => KinogoSite.UserAgent;

    public void ApplyBrowserHeaders(HttpRequestMessage msg) => KinogoSite.ApplyBrowserHeaders(msg);

    public void ApplyAjaxHeaders(HttpRequestMessage msg) => KinogoSite.ApplyAjaxHeaders(msg);

    public bool IsValidPageUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase);

    // CF challenge markers only — DLE comment captchas (turnstile.js) are
    // common on such sites and must not read as a challenge (kinogo.is lesson)
    public bool LooksLikeChallenge(string html) =>
        html.Contains("just a moment", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("cf-chl", StringComparison.OrdinalIgnoreCase);

    public string SearchCategory => "поиск";

    public string? LightSearchUrl =>
        _profile.SearchKind == "lightsearch" ? BaseUrl + "/engine/ajax/controller.php?mod=lightsearch" : null;

    public string SearchPageUrl(string query) =>
        _profile.SearchKind == "dlesearch"
            ? $"{BaseUrl}/index.php?do=search&subaction=search&story={Uri.EscapeDataString(query)}"
            : $"{BaseUrl}/search/{Uri.EscapeDataString(query)}/";

    public string? SearchRequestUrl(string query) =>
        _profile.SearchKind == "dlesearch" ? SearchPageUrl(query) : null;

    private static string? AbsoluteUrl(string baseUrl, string host, string raw)
    {
        if (raw.Length == 0)
        {
            return null;
        }

        var full = raw.StartsWith("//", StringComparison.Ordinal)
            ? "https:" + raw
            : raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? raw
                : baseUrl.TrimEnd('/') + (raw.StartsWith('/') ? raw : "/" + raw);
        full = System.Net.WebUtility.HtmlDecode(full);
        return Uri.TryCreate(full, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }

    public List<string> ExtractEmbedUrls(string filmHtml)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in Regex.Matches(
                     filmHtml, @"(?:<iframe[^>]*?|\s)(?:data-src|src)=""([^""]+)""").Cast<Match>())
        {
            var url = WebUtility.HtmlDecode(m.Groups[1].Value);
            if (url.StartsWith("//", StringComparison.Ordinal))
            {
                url = "https:" + url;
            }

            if (url.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("agl010", StringComparison.OrdinalIgnoreCase) ||
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(url))
            {
                continue;
            }

            if (url.Contains("nextembed", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("cinemar", StringComparison.OrdinalIgnoreCase))
            {
                result.Insert(0, url);
            }
            else
            {
                result.Add(url);
            }
        }

        return result;
    }

    // Parses both known DLE templates from server HTML. Cards are located by
    // their title marker (card__title / shortstoryHeader), not by container —
    // wrappers differ across DLE templates (div.card, article, custom)
    public (List<OnlineCinemaItem> Items, int TotalPages) ParseCategoryPageHtml(
        string html, string category)
    {
        var total = 1;
        foreach (var pageMatch in Regex.Matches(html, @"href=""[^""]*/page/(\d+)/""").Cast<Match>())
        {
            total = Math.Max(total, int.Parse(pageMatch.Groups[1].Value));
        }

        return _profile.Template == "card"
            ? ParseCardTemplate(html, category, total)
            : ParseShortStoryTemplate(html, category, total);
    }

    private (List<OnlineCinemaItem> Items, int TotalPages) ParseShortStoryTemplate(
        string html, string category, int total)
    {
        var items = new List<OnlineCinemaItem>();
        foreach (var card in html.Split("<article").Skip(1))
        {
            if (!card.Contains("shortstoryHeader", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var head = Regex.Match(card, @"<h2[^>]*>\s*<a href=""([^""]+)""[^>]*>([^<]+)");
            if (!head.Success)
            {
                continue;
            }

            var pageUrl = AbsoluteUrl(_profile.BaseUrl, Host, head.Groups[1].Value);
            if (pageUrl == null)
            {
                continue;
            }

            var genres = new List<string>();
            var genreMatch = Regex.Match(card, @"<b>Жанр:</b>(.*?)</span>", RegexOptions.Singleline);
            if (genreMatch.Success)
            {
                genres = Regex.Replace(genreMatch.Groups[1].Value, @"<[^>]+>", " ")
                    .Split('/')
                    .Select(s => s.Trim().ToLowerInvariant())
                    .Where(s => s.Length > 0 && M3UParserService.KnownGenres.Contains(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var yearMatch = Regex.Match(card, @"god/[^""]*""[^>]*>\s*(\d{4})");
            var posterMatch = Regex.Match(
                card, @"sPoster[^>]*>\s*<a[^>]*>\s*<img[^>]*data-src=""([^""]+)""",
                RegexOptions.Singleline);

            items.Add(new OnlineCinemaItem
            {
                PageUrl = pageUrl,
                Title = head.Groups[2].Value.Trim(),
                Year = yearMatch.Success ? int.Parse(yearMatch.Groups[1].Value) : 0,
                PosterUrl = AbsoluteUrl(_profile.BaseUrl, Host,
                                posterMatch.Success ? posterMatch.Groups[1].Value : string.Empty) ??
                            string.Empty,
                Description = Regex.Match(card, @"excerpt"">([^<]+)").Groups[1].Value.Trim(),
                Category = category
            });
        }

        return (items, total);
    }

    // Card title marker anchors each card; the chunk up to the next marker
    // carries year/genre/poster regardless of the wrapper element
    private static List<Match> TitleAnchorMatches(string html, string marker) =>
        Regex.Matches(html, marker + @"""\s*>\s*<a href=""(?<url>[^""]+)""[^>]*>(?<title>[^<]+)")
            .Cast<Match>()
            .ToList();

    private (List<OnlineCinemaItem> Items, int TotalPages) ParseCardTemplate(
        string html, string category, int total)
    {
        var items = new List<OnlineCinemaItem>();
        var anchors = TitleAnchorMatches(html, @"card__title");
        for (var i = 0; i < anchors.Count; i++)
        {
            // Chunk = this title up to the next one (or page end)
            var start = anchors[i].Index;
            var end = i + 1 < anchors.Count ? anchors[i + 1].Index : html.Length;
            var card = html.Substring(start, Math.Min(end - start, 6000));

            var pageUrl = AbsoluteUrl(_profile.BaseUrl, Host, anchors[i].Groups["url"].Value);
            if (pageUrl == null)
            {
                continue;
            }

            var yearMatch = Regex.Match(card, @"Год выпуска:</span>\s*<a[^>]*>\s*(\d{4})");
            var posterMatch = Regex.Match(card, @"<img[^>]*?src=""([^""]+)""", RegexOptions.Singleline);

            var genres = new List<string>();
            var genreMatch = Regex.Match(card, @"Жанр:</span>(.*?)</li>", RegexOptions.Singleline);
            if (genreMatch.Success)
            {
                genres = Regex.Matches(genreMatch.Groups[1].Value, @"<a[^>]*>([^<]+)</a>")
                    .Cast<Match>()
                    .Select(m => m.Groups[1].Value.Trim().ToLowerInvariant())
                    .Where(s => s.Length > 0 && M3UParserService.KnownGenres.Contains(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            items.Add(new OnlineCinemaItem
            {
                PageUrl = pageUrl,
                Title = anchors[i].Groups["title"].Value.Trim(),
                Year = yearMatch.Success ? int.Parse(yearMatch.Groups[1].Value) : 0,
                PosterUrl = AbsoluteUrl(_profile.BaseUrl, Host,
                                posterMatch.Success ? posterMatch.Groups[1].Value : string.Empty) ??
                            string.Empty,
                Genres = genres,
                Description = Regex.Match(card, @"card__text[^>]*>([^<]+)").Groups[1].Value.Trim(),
                Category = category
            });
        }

        return (items, total);
    }

    // No lightsearch snippet on generic sites — search returns listing pages
    // parsed by ParseCategoryPageHtml, never this
    public List<OnlineCinemaItem> ParseLightSearchHtml(string html, string category) => [];

    public OnlineCinemaItem? ToItem(JsonElement el, string category) => KinogoSite.ToItem(el, category);

    // Runs in the page context (WebView2 fallback); covers both templates
    public string ParseCategoryPageJs => @"
(() => {
    const out = [];
    const cards = document.querySelectorAll('article.shortStory, #dle-content .card, .sect__content .card, div[class*=movie], article');
    for (const el of cards) {
        const a = el.querySelector('.shortstoryHeader h2 a') || el.querySelector('.card__title a') || el.querySelector('h2 a') || el.querySelector('h3 a');
        if (!a || !a.href) continue;
        const img = el.querySelector('.sPoster img') || el.querySelector('img');
        const infoSpans = [...el.querySelectorAll('.sInfo span')];
        const rows = [...el.querySelectorAll('li')];
        const spanVal = key => {
            const s = infoSpans.find(x => x.querySelector('b') && x.querySelector('b').textContent.includes(key));
            return s ? s.textContent.replace(s.querySelector('b').textContent, '').trim() : '';
        };
        const rowVal = key => {
            const li = rows.find(x => x.querySelector('span') && x.querySelector('span').textContent.includes(key));
            return li ? li.textContent.replace(li.querySelector('span').textContent, '').trim() : '';
        };
        const yearText = (el.querySelector(""a[href*='/god/']"")?.textContent ||
            el.querySelector(""a[href*='/filmy/']"")?.textContent || '').trim();
        const yearNum = (spanVal('Год') || rowVal('Год выпуска') || yearText).match(/\d{4}/);
        out.push({
            title: (a.textContent.trim()) || (img && img.alt && img.alt.trim()) || '',
            url: a.href,
            poster: img ? new URL(img.getAttribute('data-src') || img.src || '', location.origin).href : '',
            year: yearNum ? parseInt(yearNum[0], 10) : 0,
            genres: spanVal('Жанр') || rowVal('Жанр'),
            description: (el.querySelector('.excerpt')?.textContent ||
                el.querySelector('.card__text')?.textContent || '').trim()
        });
    }
    let total = 1;
    for (const a of document.querySelectorAll(""a[href*='/page/']"")) {
        const m = a.href.match(/\/page\/(\d+)\//);
        if (m) total = Math.max(total, parseInt(m[1], 10));
    }
    const cur = location.href.match(/\/page\/(\d+)\//);
    return { total: total, current: cur ? parseInt(cur[1], 10) : 1, items: out };
})()";
}
