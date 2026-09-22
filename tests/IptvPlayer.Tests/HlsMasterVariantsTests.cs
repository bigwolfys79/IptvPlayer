using IptvPlayer.Services;

namespace IptvPlayer.Tests;

public class HlsMasterVariantsTests
{
    private const string Sample =
        "#EXTM3U\n" +
        "#EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=2130813,RESOLUTION=854x480,FRAME-RATE=23.974,CODECS=\"avc1.4d401e,mp4a.40.2\"\n" +
        "index-f1-v1-a1.m3u8\n" +
        "#EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=4125821,RESOLUTION=1280x720,FRAME-RATE=23.974,CODECS=\"avc1.4d401f,mp4a.40.2\"\n" +
        "index-f2-v1-a1.m3u8\n" +
        "#EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=6067361,RESOLUTION=1920x1080,FRAME-RATE=23.974,CODECS=\"avc1.4d4028,mp4a.40.2\"\n" +
        "index-f3-v1-a1.m3u8\n";

    [Fact]
    public void ParseHlsMasterVariants_ExtractsResolutionLabelsAndResolvesUrls()
    {
        var result = StreamService.ParseHlsMasterVariants(Sample, new Uri("http://cdn.example.com/vod/0/master.m3u8"));

        Assert.Equal(3, result.Count);
        Assert.Equal("http://cdn.example.com/vod/0/index-f1-v1-a1.m3u8", result["480p"]);
        Assert.Equal("http://cdn.example.com/vod/0/index-f2-v1-a1.m3u8", result["720p"]);
        Assert.Equal("http://cdn.example.com/vod/0/index-f3-v1-a1.m3u8", result["1080p"]);
    }

    [Fact]
    public void ParseHlsMasterVariants_AbsoluteUrlsKept()
    {
        var playlist = "#EXTM3U\n#EXT-X-STREAM-INF:RESOLUTION=1920x1080\nhttp://other.example.com/hi.m3u8\n";
        var result = StreamService.ParseHlsMasterVariants(playlist, new Uri("http://cdn.example.com/master.m3u8"));

        Assert.Equal("http://other.example.com/hi.m3u8", result["1080p"]);
    }

    [Fact]
    public void ParseHlsMasterVariants_MediaPlaylist_ReturnsEmpty()
    {
        var playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\nseg1.ts\n";
        var result = StreamService.ParseHlsMasterVariants(playlist, new Uri("http://cdn.example.com/index.m3u8"));

        Assert.Empty(result);
    }

    [Fact]
    public void ParseHlsMasterVariants_MissingResolution_SkipsEntry()
    {
        var playlist = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100\nv.m3u8\n";
        var result = StreamService.ParseHlsMasterVariants(playlist, new Uri("http://cdn.example.com/master.m3u8"));

        Assert.Empty(result);
    }

    [Fact]
    public void ParseHlsMasterVariants_DuplicateResolution_FirstWins()
    {
        var playlist = "#EXTM3U\n" +
                       "#EXT-X-STREAM-INF:RESOLUTION=1280x720\na.m3u8\n" +
                       "#EXT-X-STREAM-INF:RESOLUTION=1280x720\nb.m3u8\n";
        var result = StreamService.ParseHlsMasterVariants(playlist, new Uri("http://cdn.example.com/master.m3u8"));

        Assert.Single(result);
        Assert.Equal("http://cdn.example.com/a.m3u8", result["720p"]);
    }
}
