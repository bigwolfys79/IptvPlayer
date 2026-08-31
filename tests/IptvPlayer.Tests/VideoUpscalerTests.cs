using IptvPlayer.Services;

namespace IptvPlayer.Tests;

public class VideoUpscalerTests
{
    [Theory]
    [InlineData(null, "Off")]
    [InlineData("", "Off")]
    [InlineData("Bogus", "Off")]
    [InlineData("sharp", "Sharp")]
    [InlineData("SDUPSCALE", "SdUpscale")]
    [InlineData("Denoise", "Denoise")]
    public void Normalize_ValidatesAndCasingFolds(string? input, string expected)
    {
        Assert.Equal(expected, VideoUpscaler.Normalize(input));
    }

    [Fact]
    public void GetFilters_Off_ReturnsNull()
    {
        Assert.Null(VideoUpscaler.GetFilters(VideoUpscaler.Off));
        Assert.Null(VideoUpscaler.GetFilters(null));
    }

    [Theory]
    [InlineData(VideoUpscaler.Sharp, "unsharp")]
    [InlineData(VideoUpscaler.Denoise, "hqdn3d")]
    [InlineData(VideoUpscaler.SdUpscale, "xbr")]
    public void GetFilters_KnownModes_ReturnFilterChain(string mode, string expectedFragment)
    {
        var filters = VideoUpscaler.GetFilters(mode);
        Assert.NotNull(filters);
        Assert.Contains(expectedFragment, filters);
    }
}
