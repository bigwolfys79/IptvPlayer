using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IptvPlayer.Services;

// Site adapter for kinogo.is — same DLE engine, newer template
// (div.card cards instead of article.shortStory, year under /filmy/<year>/,
// nextembed/stravers players). Everything is fetched over plain HTTP: the
// site answers without a challenge
public sealed class KinogoIsOnlineSite : IOnlineCinemaSite
{
    public const string SiteId = "kinogois";

    public const string SiteBaseUrl = "https://kinogo.is";

    public string Id => SiteId;

    public string BaseUrl => SiteBaseUrl;

    public string DisplayName => L.T("OnlineCinema2_Name_Lbl");

    // DLE full search: GET index.php?do=search&subaction=search&story=<q>
    // returns a normal listing page with the same .card markup
    public bool SupportsSearch => true;

    // category name -> site path; only paths verified live are listed
    public static readonly IReadOnlyDictionary<string, string> SiteCategories =
        new Dictionary<string, string>
        {
            ["все фильмы"] = "/filmy/",
            ["сериалы"] = "/serialy/",
            ["мультфильмы"] = "/multfilmy/",
            ["боевик"] = "/boeviki/",
            ["комедия"] = "/komedii/",
            ["драма"] = "/dramy/",
            ["триллер"] = "/trillery/",
            ["ужасы"] = "/uzhasy/",
            ["фантастика"] = "/fantastika/",
            ["фэнтези"] = "/fjentezi/",
            ["приключения"] = "/prikljuchenija/",
            ["криминал"] = "/kriminal/",
            ["военный"] = "/voennye/",
            ["документальные"] = "/dokumentalnye/",
            ["детектив"] = "/detektivy/",
            ["исторические"] = "/istoricheskie/",
            ["мелодрама"] = "/melodramy/",
            ["вестерн"] = "/vesterny/",
            ["мюзикл"] = "/mjuzikly/"
        };

    public IReadOnlyDictionary<string, string> Categories => SiteCategories;

    public string CategoryUrl(string category, int page)
    {
        var path = SiteCategories.TryGetValue(category, out var p) ? p : "/filmy/";
        return page <= 1 ? SiteBaseUrl + path : $"{SiteBaseUrl}{path}page/{page}/";
    }

    // Year listing is a plain category subpath on this site
    public string YearUrl(int year, int page = 1) =>
        page <= 1 ? $"{SiteBaseUrl}/filmy/{year}/" : $"{SiteBaseUrl}/filmy/{year}/page/{page}/";

    public string UserAgent => KinogoSite.UserAgent;

    public void ApplyBrowserHeaders(HttpRequestMessage msg) => KinogoSite.ApplyBrowserHeaders(msg);

    public void ApplyAjaxHeaders(HttpRequestMessage msg) => KinogoSite.ApplyAjaxHeaders(msg);

    public static bool IsValidSitePageUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("kinogo.is", StringComparison.OrdinalIgnoreCase);

    public bool IsValidPageUrl(string url) => IsValidSitePageUrl(url);

    // This site carries a DLE comment-captcha script (/engine/modules/
    // kinogo_turnstile/turnstile.js) on every film page — the word "turnstile"
    // there is NOT a Cloudflare challenge, so only the strong CF markers count
    public bool LooksLikeChallenge(string html) =>
        html.Contains("just a moment", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("cf-chl", StringComparison.OrdinalIgnoreCase);

    public string SearchCategory => "поиск";

    public string? LightSearchUrl => null;

    public string SearchPageUrl(string query) =>
        $"{SiteBaseUrl}/index.php?do=search&subaction=search&story={Uri.EscapeDataString(DleSearchQuery(query))}";

    public string? SearchRequestUrl(string query) => SearchPageUrl(query);

    // DLE full search matches whole words; the trailing wildcard enables
    // prefix matches on boolean-mode engines and is ignored by the rest
    internal static string DleSearchQuery(string query) =>
        query.EndsWith('*') ? query : query + "*";

    private static string? AbsoluteUrl(string raw)
    {
        if (raw.Length == 0)
        {
            return null;
        }

        var full = raw.StartsWith("//", StringComparison.Ordinal)
            ? "https:" + raw
            : raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? raw
                : SiteBaseUrl + raw;
        full = WebUtility.HtmlDecode(full);
        return IsValidSitePageUrl(full) ? full : null;
    }

    // Embed iframes: this template writes protocol-relative data-src
    // (//api.nextembed.ws/...) and HTML-escapes ampersands — both are
    // normalized here. nextembed goes first (its config carries a plain
    // master .m3u8 the resolver already understands)
    public List<string> ExtractEmbedUrls(string filmHtml)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in Regex.Matches(
                     filmHtml, @"(?:<iframe[^>]*?|\s)(?:data-src|src)=""([^""]+)""").Cast<Match>())
        {
            AddEmbedUrl(m.Groups[1].Value, result, seen);
        }

        return result;
    }

    private static void AddEmbedUrl(string raw, List<string> result, HashSet<string> seen)
    {
        var url = WebUtility.HtmlDecode(raw);
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }

        if (url.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("agl010", StringComparison.OrdinalIgnoreCase) ||
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            !seen.Add(url))
        {
            return;
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

    // Parses the category/year page server HTML: div.card cards with a
    // card__title link, a plain <img src> poster, and a card__list of
    // "label: value" rows
    public (List<OnlineCinemaItem> Items, int TotalPages) ParseCategoryPageHtml(
        string html, string category)
    {
        var items = new List<OnlineCinemaItem>();
        var total = 1;

        foreach (var pageMatch in Regex.Matches(html, @"href=""[^""]*/page/(\d+)/""").Cast<Match>())
        {
            total = Math.Max(total, int.Parse(pageMatch.Groups[1].Value));
        }

        foreach (var card in html.Split("<div class=\"card d-flex\">").Skip(1))
        {
            var head = Regex.Match(card, @"card__title"">\s*<a href=""([^""]+)""[^>]*>([^<]+)");
            if (!head.Success)
            {
                continue;
            }

            var pageUrl = AbsoluteUrl(head.Groups[1].Value);
            if (pageUrl == null)
            {
                continue;
            }

            var yearMatch = Regex.Match(card, @"Год выпуска:</span>\s*<a[^>]*>\s*(\d{4})");
            var posterMatch = Regex.Match(card, @"card__img[^>]*>\s*<img[^>]*?src=""([^""]+)""", RegexOptions.Singleline);

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
                Title = head.Groups[2].Value.Trim(),
                Year = yearMatch.Success ? int.Parse(yearMatch.Groups[1].Value) : 0,
                PosterUrl = AbsoluteUrl(posterMatch.Success ? posterMatch.Groups[1].Value : string.Empty) ??
                            string.Empty,
                Genres = genres,
                Description = Regex.Match(card, @"card__text[^>]*>([^<]+)").Groups[1].Value.Trim(),
                Category = category
            });
        }

        return (items, total);
    }

    // The template has no lightsearch snippet cards — quick search is not
    // supported, the caller never invokes this
    public List<OnlineCinemaItem> ParseLightSearchHtml(string html, string category) => [];

    public OnlineCinemaItem? ToItem(JsonElement el, string category) => KinogoSite.ToItem(el, category);

    // Runs in the page context (WebView2 fallback): div.card cards, the
    // card__list rows carry year and genres
    public const string SiteParseCategoryPageJs = @"
(() => {
    const out = [];
    for (const el of document.querySelectorAll('.sect__content .card, #dle-content .card')) {
        const a = el.querySelector('.card__title a');
        const img = el.querySelector('.card__img img');
        const rows = [...el.querySelectorAll('.card__list li')];
        const rowVal = key => {
            const li = rows.find(x => x.querySelector('span') && x.querySelector('span').textContent.includes(key));
            return li ? li.textContent.replace(li.querySelector('span').textContent, '').trim() : '';
        };
        const yearText = (el.querySelector("".card__list a[href*='/filmy/']"")?.textContent || '').trim();
        out.push({
            title: (a && a.textContent.trim()) || (img && img.alt && img.alt.trim()) || '',
            url: (a && a.href) || '',
            poster: img ? new URL(img.getAttribute('data-src') || img.src || '', location.origin).href : '',
            year: parseInt(yearText, 10) || 0,
            genres: rowVal('Жанр'),
            description: (el.querySelector('.card__text')?.textContent || '').trim()
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

    public string ParseCategoryPageJs => SiteParseCategoryPageJs;
}
