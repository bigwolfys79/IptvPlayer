using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IptvPlayer.Services;

// Site adapter for the online-cinema source. Selector set mirrors the proven reference parser
// (article.shortStory cards, /page/N/ pagination, embed iframe, api/playlist/load).
// New sites plug in as another class with the same shape.
public static class KinogoSite
{
    public const string Id = "kinogo";
    public const string BaseUrl = "https://kinogo.online";

    // category name -> site path (page N appended as /page/N/)
    public static readonly IReadOnlyDictionary<string, string> Categories = new Dictionary<string, string>
    {
        ["все фильмы"] = "/filmy/",
        ["мультфильмы"] = "/multfilmy/",
        ["новинки"] = "/novinki/",
        ["фантастика"] = "/fantastika/",
        ["фэнтези"] = "/fjentezi/",
        ["ужасы"] = "/uzhasy/",
        ["триллер"] = "/triller/",
        ["спорт"] = "/sport/",
        ["приключения"] = "/prikljuchenija/",
        ["исторические"] = "/istoricheskie/",
        ["мелодрама"] = "/melodrama/",
        ["короткометражка"] = "/korotkometrazhka/",
        ["криминал"] = "/kriminal/",
        ["драма"] = "/drama/",
        ["комедия"] = "/komedija/",
        ["документальные"] = "/dokumentalnye/",
        ["детектив"] = "/detektiv/",
        ["детский"] = "/detskij/",
        ["военный"] = "/voennyj/",
        ["вестерн"] = "/vestern/",
        ["мюзикл"] = "/mjuzikl/",
        ["нуар"] = "/nuar/"
    };

    public static string CategoryUrl(string category, int page)
    {
        var path = Categories.TryGetValue(category, out var p) ? p : "/filmy/";
        return page <= 1 ? BaseUrl + path : $"{BaseUrl}{path}page/{page}/";
    }

    public static string YearUrl(int year, int page = 1) =>
        page <= 1 ? $"{BaseUrl}/xfsearch/god/{year}/" : $"{BaseUrl}/xfsearch/god/{year}/page/{page}/";

    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    // Header set that passes the site WAF: it allows requests looking like
    // in-site navigation (Referer + Sec-Fetch-Site: same-origin) — verified live
    public static void ApplyBrowserHeaders(HttpRequestMessage msg)
    {
        msg.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        msg.Headers.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        msg.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9");
        msg.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
        msg.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        msg.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        msg.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        msg.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        msg.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
    }

    public static bool LooksLikeChallenge(string html) =>
        html.Contains("just a moment", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("cf-chl", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("turnstile", StringComparison.OrdinalIgnoreCase);

    // Embed player iframes from the film page HTML (data-src or src); ads and
    // YouTube are skipped, the main provider goes first. Used by the pure-HTTP resolver
    public static List<string> ExtractEmbedUrls(string filmHtml)
    {
        var result = new List<string>();
        foreach (var m in Regex.Matches(filmHtml, @"<iframe[^>]*?(?:data-src|src)=""([^""]+)""").Cast<Match>())
        {
            var url = m.Groups[1].Value;
            if (url.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("agl010", StringComparison.OrdinalIgnoreCase) ||
                !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            if (url.Contains("cinemar", StringComparison.OrdinalIgnoreCase))
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

    // Only https pages of the site itself may be fetched/parsed
    public static bool IsValidPageUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("kinogo.online", StringComparison.OrdinalIgnoreCase);

    private static string? AbsoluteUrl(string raw)
    {
        if (raw.Length == 0)
        {
            return null;
        }

        var full = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? raw
            : BaseUrl + raw;
        return IsValidPageUrl(full) ? full : null;
    }

    // Parses the category/xfsearch page server HTML (same template the in-page
    // JS parser sees): film cards + pagination total. Used by the pure-HTTP
    // catalog sync — no browser needed for catalog collection
    public static (List<OnlineCinemaItem> Items, int TotalPages) ParseCategoryPageHtml(
        string html, string category)
    {
        var items = new List<OnlineCinemaItem>();
        var total = 1;

        foreach (var pageMatch in Regex.Matches(html, @"href=""[^""]*/page/(\d+)/""").Cast<Match>())
        {
            total = Math.Max(total, int.Parse(pageMatch.Groups[1].Value));
        }

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

            var pageUrl = AbsoluteUrl(head.Groups[1].Value);
            if (pageUrl == null)
            {
                continue;
            }

            var genres = new List<string>();
            var genreMatch = Regex.Match(card, @"<b>Жанр:</b>(.*?)</span>", RegexOptions.Singleline);
            if (genreMatch.Success)
            {
                var clean = Regex.Replace(genreMatch.Groups[1].Value, @"<[^>]+>", " ");
                genres = clean.Split('/')
                    .Select(s => s.Trim().ToLowerInvariant())
                    .Where(s => s.Length > 0 && M3UParserService.KnownGenres.Contains(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var yearMatch = Regex.Match(card, @"god/[^""]*""[^>]*>\s*(\d{4})");
            var posterMatch = Regex.Match(card, @"sPoster[^>]*>\s*<a[^>]*>\s*<img[^>]*data-src=""([^""]+)""", RegexOptions.Singleline);

            items.Add(new OnlineCinemaItem
            {
                PageUrl = pageUrl,
                Title = head.Groups[2].Value.Trim(),
                Year = yearMatch.Success ? int.Parse(yearMatch.Groups[1].Value) : 0,
                PosterUrl = AbsoluteUrl(posterMatch.Success ? posterMatch.Groups[1].Value : string.Empty) ?? string.Empty,
                Genres = genres,
                Description = Regex.Match(card, @"excerpt"">([^<]+)").Groups[1].Value.Trim(),
                Category = category
            });
        }

        return (items, total);
    }

    // Runs in the page context; returns the card list and the pagination total.
    // Must evaluate to a plain JSON-serializable object (ExecuteScriptAsync wraps it).
    public const string ParseCategoryPageJs = @"
(() => {
    const out = [];
    for (const el of document.querySelectorAll('article.shortStory')) {
        const a = el.querySelector('.shortstoryHeader h2 a');
        const img = el.querySelector('.sPoster img');
        const infoSpans = [...el.querySelectorAll('.sInfo span')];
        const spanVal = key => {
            const s = infoSpans.find(x => x.querySelector('b') && x.querySelector('b').textContent.includes(key));
            return s ? s.textContent.replace(s.querySelector('b').textContent, '').trim() : '';
        };
        const yearText = (el.querySelector("".sInfo a[href*='/god/']"")?.textContent || '').trim();
        out.push({
            title: (a && a.textContent.trim()) || (img && img.alt && img.alt.trim()) || '',
            url: (a && a.href) || '',
            poster: img ? new URL(img.getAttribute('data-src') || img.src || '', location.origin).href : '',
            year: parseInt(yearText, 10) || 0,
            genres: spanVal('Жанр'),
            description: (el.querySelector('.sInfo .excerpt')?.textContent || '').trim()
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

    // Applies the site's session year filter (same POST as reference parser);
    // __YEAR__ is substituted in code (ExecuteScriptAsync takes no arguments)
    public const string SetYearFilterJsTemplate = @"
async () => {
    await fetch(location.href, {
        method: 'POST', credentials: 'include',
        headers: { 'content-type': 'application/x-www-form-urlencoded; charset=UTF-8', 'x-requested-with': 'XMLHttpRequest' },
        body: 'xsort=1&xs_field=year&xs_value=__YEAR__'
    });
    return true;
}";

    // All embed player candidates on the page (the site hosts several providers,
    // e.g. several providers); ads and YouTube are skipped.
    // Each candidate is scrolled into view first — the player sits below the
    // fold, and the click aim is viewport-relative
    public const string IframeCandidatesJs = @"
(() => {
    const list = [...document.querySelectorAll('iframe[data-src]')]
        .filter(f => { const u = f.dataset.src || ''; return u && !/youtube|agl010/i.test(u); });
    return list.map(f => {
        f.scrollIntoView({ block: 'center' });
        const r = f.getBoundingClientRect();
        return { x: r.x + r.width / 2, y: r.y + r.height / 2, w: r.width, h: r.height, src: (f.dataset.src || '').slice(0, 60) };
    });
})()";

    public const string HasYouTubeOnlyJs = @"
(() => {
    const f = [...document.querySelectorAll('iframe')].find(f =>
        ((f.getAttribute('data-src') || '') + (f.src || '')).includes('youtube.com/embed'));
    return f ? ((f.getAttribute('data-src') || '') + (f.src || '')) : null;
})()";

    public static OnlineCinemaItem? ToItem(JsonElement el, string category)
    {
        var title = el.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var url = el.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
        if (title.Length == 0 || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var genres = new List<string>();
        if (el.TryGetProperty("genres", out var g))
        {
            var raw = g.GetString() ?? string.Empty;
            genres = raw.Split('/')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return new OnlineCinemaItem
        {
            PageUrl = url,
            Title = title,
            Year = el.TryGetProperty("year", out var y) && y.TryGetInt32(out var year) ? year : 0,
            PosterUrl = el.TryGetProperty("poster", out var p) ? p.GetString() ?? string.Empty : string.Empty,
            Genres = genres,
            Description = el.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty,
            Category = category
        };
    }
}
