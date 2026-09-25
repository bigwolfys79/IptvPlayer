using IptvPlayer.Services;

namespace IptvPlayer.Tests;

public class KinogoSiteHtmlTests
{
    private const string Page = @"
<html><body>
<nav><a href=""https://kinogo.online/filmy/page/7/"">7</a><a href=""https://kinogo.online/filmy/page/11/"">11</a></nav>
<article class=""shortStory"">
  <div class=""shortstoryHeader d-flex ai-center""><div class=""hTitle"">
    <h2 style=""max-width:475px""><a href=""https://kinogo.online/filmy/118113-gipoteza-ljubvi.html"" title=""Гипотеза любви (2026)"">Гипотеза любви (2026) </a></h2>
  </div></div>
  <div class=""shortstoryBody""><div class=""sPoster"">
    <a href=""https://kinogo.online/filmy/118113-gipoteza-ljubvi.html"">
      <img class=""lazy"" data-src=""/uploads/mini/fullstory/896/c6c6c039197ce06be4d0434c8086c.jpg"" src=""data:image/gif;base64,R0lGODlh"" width=""200"" height=""300"" alt=""x"">
    </a></div>
    <div class=""sInfo""><span><b>Год:</b> <a href=""https://kinogo.online/god/2026/"">2026</a></span>
    <span><b>Жанр:</b> Фильмы / Новинки /  Комедия / Мелодрама</span>
    <div class=""excerpt"">Олив и Адам, два ученых, начинают фиктивные отношения.</div></div>
</div></article>
<article class=""shortStory"">
  <div class=""shortstoryHeader""><div class=""hTitle"">
    <h2><a href=""https://evil.example.com/filmy/1.html"" title=""Плохой"">Плохой</a></h2>
  </div></div>
</article>
<article class=""carousel""><div>не карточка</div></article>
</body></html>";

    [Fact]
    public void ParseCategoryPageHtml_ExtractsCardsWithFields()
    {
        var (items, total) = KinogoSite.ParseCategoryPageHtml(Page, "все фильмы");

        var item = Assert.Single(items);
        Assert.Equal("https://kinogo.online/filmy/118113-gipoteza-ljubvi.html", item.PageUrl);
        Assert.Equal("Гипотеза любви (2026)", item.Title);
        Assert.Equal("https://kinogo.online/uploads/mini/fullstory/896/c6c6c039197ce06be4d0434c8086c.jpg", item.PosterUrl);
        Assert.Equal(2026, item.Year);
        // KnownGenres whitelist: site prefixes and "Новинки" style tokens stay
        // only when whitelisted; titles never leak in
        Assert.Equal(["новинки", "комедия", "мелодрама"], item.Genres);
        Assert.Contains("фиктивные отношения", item.Description);
        Assert.Equal("все фильмы", item.Category);
        Assert.Equal(11, total);
    }

    [Theory]
    [InlineData("http://kinogo.online/filmy/1.html", false)]   // https only
    [InlineData("https://evil.example.com/filmy/1.html", false)]
    [InlineData("https://kinogo.online/filmy/1.html", true)]
    [InlineData("/filmy/1.html", false)]                       // validator takes absolute URLs only
    public void IsValidPageUrl_OnlyHttpsKinogo(string url, bool expected)
    {
        Assert.Equal(expected, KinogoSite.IsValidPageUrl(url));
    }

    [Fact]
    public void LooksLikeChallenge_DetectsCloudflareStub()
    {
        Assert.True(KinogoSite.LooksLikeChallenge("<title>Just a moment...</title>"));
        Assert.True(KinogoSite.LooksLikeChallenge("<div id=\"cf-chl-widget-123\">"));
        Assert.False(KinogoSite.LooksLikeChallenge("<title>Фильмы 2026 — КиноГо</title>"));
    }

    [Fact]
    public void YearUrl_BuildsXfsearchPath()
    {
        Assert.Equal("https://kinogo.online/xfsearch/god/2026/", KinogoSite.YearUrl(2026));
        Assert.Equal("https://kinogo.online/xfsearch/god/2026/page/3/", KinogoSite.YearUrl(2026, 3));
    }
}
