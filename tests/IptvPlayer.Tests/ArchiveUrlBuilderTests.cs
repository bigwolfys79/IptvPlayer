using IptvPlayer.Services;

namespace IptvPlayer.Tests;

public class ArchiveUrlBuilderTests
{
    [Fact]
    public void BuildUrl_WithoutQuery_AddsQuestionMark()
    {
        var url = ArchiveUrlBuilder.BuildUrl("http://example.com/stream.m3u8", new DateTime(2026, 1, 1, 12, 0, 0));
        Assert.StartsWith("http://example.com/stream.m3u8?", url);
    }

    [Fact]
    public void BuildUrl_WithExistingQuery_AddsAmpersand()
    {
        var url = ArchiveUrlBuilder.BuildUrl("http://example.com/stream.m3u8?token=abc", new DateTime(2026, 1, 1, 12, 0, 0));
        Assert.StartsWith("http://example.com/stream.m3u8?token=abc&", url);
        Assert.DoesNotContain("??", url);
    }

    [Fact]
    public void BuildUrl_ContainsUtcAndLutcParameters()
    {
        var programStart = new DateTime(2026, 1, 1, 12, 0, 0);
        var url = ArchiveUrlBuilder.BuildUrl("http://example.com/stream.m3u8", programStart);

        Assert.Contains("utc=", url);
        Assert.Contains("lutc=", url);

        // utc — epoch-секунды начала передачи в локальном времени.
        var expectedUtc = new DateTimeOffset(programStart).ToUnixTimeSeconds();
        Assert.Contains($"utc={expectedUtc}", url);
    }

    [Fact]
    public void BuildUrl_LutcIsNow()
    {
        var before = DateTimeOffset.Now.ToUnixTimeSeconds();
        var url = ArchiveUrlBuilder.BuildUrl("http://example.com/stream.m3u8", DateTime.Now.AddHours(-1));
        var after = DateTimeOffset.Now.ToUnixTimeSeconds();

        var lutc = long.Parse(url.Split("lutc=")[1]);
        Assert.InRange(lutc, before, after);
    }
}
