using IptvPlayer.Services;

namespace IptvPlayer.Tests;

public class OnlineCinemaDownloadHtmlTests
{
    // Live sample: anchor labels are shifted (the "1080p HD" link carries
    // /720.mp4) — the parser must take the quality from the URL suffix
    private const string Window = @"
<div class=""download-window"">
	<div class=""download-header"">Скачать видео<button class=""download-close""></button></div>
	<div class=""download-content"">
		<a href=""https://host.cinemap.cc/cb2ec3fab0c307ba5765206aa68fb21d:2026092710/tvseries/407f56f44b08ec3ab2f32ad7b9c297ec7f506515/1080.mp4"" target=""_blank"">
			1080p
			<span>FullHD</span>		</a>
		<a href=""https://host.cinemap.cc/3cd122a6a006ff97f94cd69bce07d768:2026092710/tvseries/407f56f44b08ec3ab2f32ad7b9c297ec7f506515/720.mp4"" target=""_blank"">
			1080p
			<span>HD</span>		</a>
		<a href=""https://host.cinemap.cc/ba6540f08944607c12d928e2d76805a3:2026092710/tvseries/407f56f44b08ec3ab2f32ad7b9c297ec7f506515/480.mp4"" target=""_blank"">
			720p
					</a>
	</div>
</div>";

    [Fact]
    public void ParseDownloadWindowHtml_TakesQualityFromUrlSuffix()
    {
        var options = OnlineCinemaStreamResolver.ParseDownloadWindowHtml(Window);

        Assert.Equal(3, options.Count);
        // Sorted descending by height
        Assert.Equal(new[] { "1080p", "720p", "480p" }, options.Select(o => o.Quality).ToArray());
        Assert.Contains("/1080.mp4", options[0].Url);
        Assert.Contains("/720.mp4", options[1].Url);
    }

    [Fact]
    public void ParseDownloadWindowHtml_DeduplicatesByHeight()
    {
        const string html = @"<a href=""https://host/x/1080.mp4"">a</a><a href=""https://host/y/1080.mp4?e=1"">b</a>";

        var options = OnlineCinemaStreamResolver.ParseDownloadWindowHtml(html);

        var option = Assert.Single(options);
        Assert.Equal("1080p", option.Quality);
        Assert.Equal("https://host/x/1080.mp4", option.Url);
    }

    [Fact]
    public void ParseDownloadWindowHtml_EmptyAndForeignMarkup_YieldNothing()
    {
        Assert.Empty(OnlineCinemaStreamResolver.ParseDownloadWindowHtml(string.Empty));
        Assert.Empty(OnlineCinemaStreamResolver.ParseDownloadWindowHtml(@"<a href=""https://host/video.mkv"">x</a>"));
        Assert.Empty(OnlineCinemaStreamResolver.ParseDownloadWindowHtml(@"<a href=""relative/720.mp4"">x</a>"));
    }

    [Fact]
    public void BuildFileName_SanitizesInvalidChars()
    {
        var name = OnlineCinemaDownloadService.BuildFileName(
            new[] { "Фильм: <тест>?", "1 сезон", "1 серия", "Дубляж/Озвучка" }, "720p");

        Assert.DoesNotContain("<", name);
        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain("/", name);
        Assert.EndsWith(" [720p].mp4", name);
    }
}
