using IptvPlayer.Services;

namespace IptvPlayer.Tests;

// ExtractAudioNames reads the site player's voiceover menu labels from the
// nextembed film config (audio: {"names":[...], "order":[...]}) — they align
// with the master's EXT-X-MEDIA members by index
public class OnlineCinemaAudioNamesTests
{
    private const string FilmConfigFragment =
        "hls: \"https://cdn.example.com/v/master.m3u8?t=1\",\r\n" +
        "\t\t\taudio: {\"names\":[\"Рус. Дублированный\",\"HDrezka Studio (Дубл.)\",\"HDRezka Studio\"," +
        "\"HDrezka Studio (18+)\",\"М. Яроцкий\",\"DniproFilm (укр)\",\"Колодій Трейлерів\",\"Eng.Original\"]," +
        "\"order\":[0,1,2,3,4,5,6,7]},\r\n" +
        "\t\t\tcc: [{\"url\":\"https://cdn.example.com/cc/sub.vtt?x=1\"}]\r\n";

    [Fact]
    public void ExtractAudioNames_ReadsNamesInConfigOrder()
    {
        var names = OnlineCinemaStreamResolver.ExtractAudioNames(FilmConfigFragment);

        Assert.NotNull(names);
        Assert.Equal(8, names!.Count);
        Assert.Equal("Рус. Дублированный", names[0]);
        Assert.Equal("HDrezka Studio (Дубл.)", names[1]);
        Assert.Equal("М. Яроцкий", names[4]);
        Assert.Equal("DniproFilm (укр)", names[5]);
        Assert.Equal("Eng.Original", names[7]);
    }

    [Fact]
    public void ExtractAudioNames_ReturnsNullWhenConfigHasNoNames()
    {
        Assert.Null(OnlineCinemaStreamResolver.ExtractAudioNames("hls: \"https://cdn.example.com/v/master.m3u8\""));
        Assert.Null(OnlineCinemaStreamResolver.ExtractAudioNames("audio: {\"order\":[0,1]}"));
        Assert.Null(OnlineCinemaStreamResolver.ExtractAudioNames(""));
    }

    [Fact]
    public void ExtractAudioNames_DropsEmptyNameLists()
    {
        Assert.Null(OnlineCinemaStreamResolver.ExtractAudioNames("audio: {\"names\":[]}"));
    }
}
