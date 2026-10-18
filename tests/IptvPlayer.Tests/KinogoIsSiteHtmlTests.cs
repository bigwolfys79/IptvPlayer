using IptvPlayer.Services;
using Xunit;

namespace IptvPlayer.Tests;

public class KinogoIsSiteHtmlTests
{
    private const string Page = @"
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
