using IptvPlayer.ViewModels;
using Xunit;

namespace IptvPlayer.Tests;

// Resume key format: flat title for films, "title::N" for portal episodes,
// "title::sNeM" for online-cinema series trees
public class VodResumeKeyTests
{
    [Fact]
    public void FlatTitle_ForFilm()
    {
        Assert.Equal("Фильм (2026)", MainPageViewModel.VodResumeKey("Фильм (2026)", -1, -1));
    }

    [Fact]
    public void EpisodeOnly_ForPortalSeries()
    {
        Assert.Equal("Сериал::3", MainPageViewModel.VodResumeKey("Сериал", -1, 3));
    }

    [Fact]
    public void SeasonEpisode_ForOcSeries()
    {
        Assert.Equal("Сериал (2023)::s7e2", MainPageViewModel.VodResumeKey("Сериал (2023)", 7, 2));
        Assert.Equal("Сериал::s0e0", MainPageViewModel.VodResumeKey("Сериал", 0, 0));
    }

    [Theory]
    [InlineData("Сериал (2023)::s7e2", 7, 2)]
    [InlineData("Сериал::s0e0", 0, 0)]
    [InlineData("Сериал::s12e345", 12, 345)]
    [InlineData("Сериал (2026)", -1, -1)]
    [InlineData("Сериал::9", -1, -1)]
    public void ParseSeasonPart_RoundTrips(string key, int season, int episode)
    {
        var (s, e) = MainPageViewModel.ParseSeasonPart(key);
        Assert.Equal((season, episode), (s, e));
    }
}
