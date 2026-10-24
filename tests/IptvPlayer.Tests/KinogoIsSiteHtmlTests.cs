using IptvPlayer.Services;
using Xunit;

namespace IptvPlayer.Tests;

public class KinogoIsSiteHtmlTests
{
    internal const string Page = @"
<html><body>
<nav><a href=""https://kinogo.is/filmy/page/7/"">7</a><a href=""https://kinogo.is/filmy/page/11/"">11</a></nav>
<div id=""dle-content"">
<div class=""card d-flex"">
<div class=""card__title""><a href=""https://kinogo.is/4850-chelovek-pauk-novyj-den-1710.html"">Человек-паук: Новый день (2026)</a></div>
<div class=""card__img img-fit-cover ps-link"" data-href=""https://kinogo.is/4850-chelovek-pauk-novyj-den-1710.html"">
<img src=""/uploads/posts/2026-07/1785435864-356103723-chelovek-pauk-novyy-den.webp"" alt=""Человек-паук: Новый день"">
</div>
<div class=""card__desc flex-grow-1"">
<ul class=""card__list"">
<li><span>Год выпуска:</span> <a href=""https://kinogo.is/filmy/2026/"">2026</a></li>
<li><span>Жанр:</span> <a href=""https://kinogo.is/boeviki/"">Боевик</a>,  <a href=""https://kinogo.is/fantastika/"">Фантастика</a></li>
</ul>
<p class=""card__text line-clamp"">Когда Доктор Стрэндж произнес роковое заклинание.</p>
</div>
</div>
<div class=""card d-flex"">
<div class=""card__title""><a href=""https://evil.example.com/1.html"">Плохой</a></div>
</div>
</div>
</body></html>";

    private readonly KinogoIsOnlineSite _site = new();

    [Fact]
    public void ParseCategoryPageHtml_ExtractsCardsWithFields()
    {
        var (items, total) = _site.ParseCategoryPageHtml(Page, "все фильмы");

        var item = Assert.Single(items);
        Assert.Equal("Человек-паук: Новый день (2026)", item.Title);
        Assert.Equal("https://kinogo.is/4850-chelovek-pauk-novyj-den-1710.html", item.PageUrl);
        Assert.Equal(2026, item.Year);
        Assert.Equal("https://kinogo.is/uploads/posts/2026-07/1785435864-356103723-chelovek-pauk-novyy-den.webp",
            item.PosterUrl);
        Assert.Equal(["боевик", "фантастика"], item.Genres);
        Assert.Contains("Доктор Стрэндж", item.Description);
        Assert.Equal(11, total);
    }

    [Fact]
    public void ExtractEmbedUrls_ProtocolRelativeFirst()
    {
        var html = @"
<iframe data-src=""https://tazaromikaz.link/balancer-api/iframe?kp=1&amp;token=x""></iframe>
<iframe data-src=""//api.nextembed.ws/embed/movie/90039""></iframe>
<iframe data-src=""https://www.youtube.com/embed/IAdqvI9ebk0""></iframe>
<iframe src=""http://insecure.example.com/e""></iframe>";

        var urls = _site.ExtractEmbedUrls(html);

        Assert.Equal("https://api.nextembed.ws/embed/movie/90039", urls[0]);
        Assert.Equal("https://tazaromikaz.link/balancer-api/iframe?kp=1&token=x", urls[1]);
        Assert.Equal(2, urls.Count);
    }

    [Fact]
    public void CategoryUrl_And_YearUrl_MatchSitePaths()
    {
        Assert.Equal("https://kinogo.is/filmy/", _site.CategoryUrl("все фильмы", 1));
        Assert.Equal("https://kinogo.is/serialy/page/3/", _site.CategoryUrl("сериалы", 3));
        Assert.Equal("https://kinogo.is/filmy/2024/", _site.YearUrl(2024));
        Assert.Equal("https://kinogo.is/filmy/2024/page/2/", _site.YearUrl(2024, 2));
    }

    [Fact]
    public void LooksLikeChallenge_IgnoresDleTurnstileCaptcha()
    {
        // The comment-captcha script name must not read as a Cloudflare challenge
        var html = "<script src=\"/engine/modules/kinogo_turnstile/turnstile.js\"></script>";
        Assert.False(_site.LooksLikeChallenge(html));
        Assert.True(_site.LooksLikeChallenge("<title>Just a moment...</title>"));
        Assert.True(_site.LooksLikeChallenge("cf-chl-token=..."));
    }

    [Fact]
    public void Registry_PicksAdapterByHost()
    {
        Assert.Same(OnlineCinemaSites.KinogoIs, OnlineCinemaSites.ByUrl("https://kinogo.is/4850-x.html"));
        Assert.Same(OnlineCinemaSites.Kinogo, OnlineCinemaSites.ByUrl("https://kinogo.online/filmy/"));
        Assert.Null(OnlineCinemaSites.ByUrl("https://example.com/"));
    }
}

public class GenericDleSiteTests
{
    private static OnlineCinemaSiteProfile Profile() => new()
    {
        BaseUrl = "https://kinogo.is",
        Template = "card",
        Categories = new Dictionary<string, string> { ["все фильмы"] = "/filmy/", ["сериалы"] = "/serialy/" },
        YearPathPattern = "/filmy/{0}/",
        SearchKind = "dlesearch"
    };

    [Fact]
    public void GenericAdapter_ParsesCardTemplate_FromProfile()
    {
        var site = new GenericDleSite(Profile());
        var (items, total) = site.ParseCategoryPageHtml(KinogoIsSiteHtmlTests.Page, "все фильмы");
        var item = Assert.Single(items);
        Assert.Equal("Человек-паук: Новый день (2026)", item.Title);
        Assert.Equal(11, total);
        Assert.Equal("dle:kinogo.is", site.Id);
    }

    [Fact]
    public void GenericAdapter_ParsesCardTemplate_WithoutWrapperDiv()
    {
        // mba-style markup: card__title anchors inside an unknown container
        var html = @"<div id=""dle-content""><article class=""movie"">
<div class=""card__title""><a href=""/filmy/111729-genialnyj-plan-2026.html"">Гениальный план (2026)</a></div>
<li><span>Год выпуска:</span> <a href=""/filmy/2026/"">2026</a></li>
<li><span>Жанр:</span> <a href=""/boeviki/"">Боевик</a></li>
<img src=""/uploads/posts/2026/x.webp"" alt=""Гениальный план"">
</article>
<article class=""movie""><div class=""card__title""><a href=""/filmy/89999-x.html"">Второй (2026)</a></div></article></div>";
        var site = new GenericDleSite(Profile());
        var (items, _) = site.ParseCategoryPageHtml(html, "все фильмы");
        Assert.Equal(2, items.Count);
        Assert.Equal("Гениальный план (2026)", items[0].Title);
        Assert.Equal("https://kinogo.mba2.invalid".Replace("mba2.invalid", "is") + "/filmy/111729-genialnyj-plan-2026.html", items[0].PageUrl);
        Assert.Equal(2026, items[0].Year);
        Assert.Equal(["боевик"], items[0].Genres);
    }

    [Fact]
    public void GenericAdapter_Urls_FollowProfile()
    {
        var site = new GenericDleSite(Profile());
        Assert.Equal("https://kinogo.is/filmy/2024/", site.YearUrl(2024));
        Assert.Equal("https://kinogo.is/serialy/page/3/", site.CategoryUrl("сериалы", 3));
        // DLE search carries the prefix wildcard (escaped as %2A)
        Assert.Equal("https://kinogo.is/index.php?do=search&subaction=search&story=%D0%BC%D0%B0%D1%82%D1%80%D0%B8%D1%86%D0%B0%2A",
            site.SearchPageUrl("матрица"));
    }

    [Fact]
    public void DleSearchQuery_AppendsWildcardOnce()
    {
        Assert.Equal("невск*", KinogoIsOnlineSite.DleSearchQuery("невск"));
        Assert.Equal("невск*", KinogoIsOnlineSite.DleSearchQuery("невск*"));
    }

    [Fact]
    public void GenericAdapter_YearUrl_MbaDoubleSlashPattern()
    {
        // kinogo.mba uses /filmy/filmy-{year}/, not /filmy/{year}/
        var mbaProfile = new OnlineCinemaSiteProfile
        {
            BaseUrl = "https://kinogo.mba",
            Template = "card",
            Categories = new Dictionary<string, string> { ["все фильмы"] = "/filmy/", ["сериалы"] = "/serialy/" },
            YearPathPattern = "/filmy/filmy-{0}/",
            SearchKind = "dlesearch"
        };
        var site = new GenericDleSite(mbaProfile);
        Assert.Equal("https://kinogo.mba/filmy/filmy-2024/", site.YearUrl(2024));
        Assert.Equal("https://kinogo.mba/filmy/filmy-2024/page/3/", site.YearUrl(2024, 3));
        Assert.NotEqual(site.YearUrl(2024), site.CategoryUrl("все фильмы", 1));
    }

    [Fact]
    public void GenericAdapter_HasYearListing_FollowsProfilePattern()
    {
        // Pattern present -> deep pages may take the year from the URL
        Assert.True(new GenericDleSite(Profile()).HasYearListing);

        var noYear = new GenericDleSite(new OnlineCinemaSiteProfile
        {
            BaseUrl = "https://kinogo.mba",
            Template = "card",
            Categories = new Dictionary<string, string> { ["все фильмы"] = "/filmy/" },
            SearchKind = "none"
        });
        Assert.False(noYear.HasYearListing);
        // Without a pattern YearUrl falls back to the plain category listing
        Assert.Equal(noYear.CategoryUrl("все фильмы", 2), noYear.YearUrl(2024, 2));
    }

    [Fact]
    public void GenericAdapter_ParsesCardTemplate_MbaYearLinksInCards()
    {
        // mba puts year as /filmy/2026/ inside cards, not /god/2026/
        var html = @"<div id=""dle-content""><article class=""card d-flex"">
<h2 class=""card__title""><a href=""/filmy/111729-genialnyj-plan-2026.html"">Гениальный план (2026)</a></h2>
<ul class=""card__list"">
<li><span>Год выпуска:</span> <a href=""/filmy/2026/"">2026</a></li>
<li><span>Жанр:</span> <a href=""/boeviki/"">Боевик</a>,  <a href=""/fantastika/"">Фантастика</a></li>
</ul>
<img src=""/uploads/posts/2026/x.webp"" alt=""Гениальный план"">
</article></div>";
        var mbaProfile = new OnlineCinemaSiteProfile
        {
            BaseUrl = "https://kinogo.mba",
            Template = "card",
            Categories = new Dictionary<string, string> { ["все фильмы"] = "/filmy/" },
            YearPathPattern = "/filmy/filmy-{0}/",
            SearchKind = "dlesearch"
        };
        var site = new GenericDleSite(mbaProfile);
        var (items, _) = site.ParseCategoryPageHtml(html, "все фильмы");
        Assert.Single(items);
        Assert.Equal("Гениальный план (2026)", items[0].Title);
        Assert.Equal(2026, items[0].Year);
        Assert.Equal(["боевик", "фантастика"], items[0].Genres);
    }

    [Fact]
    public void GenericAdapter_ParsesCardTemplate_MbaPremiereRowAndCommaGenres()
    {
        // Real kinogo.mba markup: no "Год выпуска" row (year sits in the
        // "Премьера" date and the title), genre row is plain comma text
        var html = @"<div id=""dle-content"" class=""sect__content"">
<article class=""card d-flex"">
    <h2 class=""card__title""><a href=""https://kinogo.mba/filmy/112000-tykvogolovyj-2-2026.html"">Тыквоголовый Р. Л. Стайна 2 (2026)</a></h2>
    <a class=""card__img img-fit-cover"" href=""https://kinogo.mba/filmy/112000-tykvogolovyj-2-2026.html"">
        <img width=""200px"" height=""300px"" src=""/uploads/mini/poster_foolstory/77/1792189773.jpg"" alt=""Тыквоголовый 2 (2026)"">
    </a>
    <div class=""card__desc flex-grow-1"">
        <ul class=""card__list"">
            <li><span>Страна:</span> <a href=""https://kinogo.mba/xfsearch/country/us/"">США</a></li>
            <li><span>Продолжительность:</span> 93</li>
            <li><span>Премьера:</span> 31 октября 2026</li>
            <li><span>Жанр:</span> фильм, ужасы, фэнтези</li>
            <li class=""pmovie__header-list--accent""><span>Качество:</span> <span>WEB-DL</span></li>
            <li class=""tarnslator""><span>Озвучка:</span> многоголосый закадровый</li>
        </ul>
        <p class=""card__text line-clamp"">«Тыквоголовый 2» (2026) — канадский фантастический хоррор.</p>
    </div>
</article></div>";
        var site = new GenericDleSite(new OnlineCinemaSiteProfile
        {
            BaseUrl = "https://kinogo.mba",
            Template = "card",
            Categories = new Dictionary<string, string> { ["все фильмы"] = "/filmy/" },
            YearPathPattern = "/filmy/filmy-{0}/",
            SearchKind = "dlesearch"
        });
        var (items, _) = site.ParseCategoryPageHtml(html, "все фильмы");
        var item = Assert.Single(items);
        Assert.Equal("Тыквоголовый Р. Л. Стайна 2 (2026)", item.Title);
        Assert.Equal(2026, item.Year);
        Assert.Equal(["ужасы", "фэнтези"], item.Genres);
    }

    [Fact]
    public void GenericAdapter_ParsesCardTemplate_TitleYearFallback()
    {
        // No Премьера row either — the year comes from the "(Year)" title
        var html = @"<div id=""dle-content""><article class=""card d-flex"">
<h2 class=""card__title""><a href=""/filmy/1-x-1998.html"">Триган (1998)</a></h2>
<img src=""/uploads/x.jpg"" alt=""Триган"">
</article></div>";
        var site = new GenericDleSite(new OnlineCinemaSiteProfile
        {
            BaseUrl = "https://kinogo.mba",
            Template = "card",
            Categories = new Dictionary<string, string> { ["все фильмы"] = "/filmy/" }
        });
        var (items, _) = site.ParseCategoryPageHtml(html, "все фильмы");
        var item = Assert.Single(items);
        Assert.Equal(1998, item.Year);
        Assert.Empty(item.Genres);
    }

    [Fact]
    public void ProbeProfile_VersionStampAndAliases()
    {
        // Probe bump: legacy profiles (Version < Current) re-probe on open
        Assert.Equal(2, OnlineCinemaSiteProbe.CurrentProfileVersion);
    }
}
