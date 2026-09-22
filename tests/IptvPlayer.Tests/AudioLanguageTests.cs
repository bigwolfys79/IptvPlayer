using IptvPlayer.Services;

namespace IptvPlayer.Tests;

public class AudioLanguageTests
{
    [Fact]
    public void SelectPreferredAudioIndex_MatchesByIsoCode()
    {
        var langs = new List<string?> { "ukr", "rus", "eng" };
        Assert.Equal(1, StreamService.SelectPreferredAudioIndex(langs, "rus"));
    }

    [Fact]
    public void SelectPreferredAudioIndex_RespectsPriorityOrder()
    {
        var langs = new List<string?> { "eng", "ukr", "rus" };
        Assert.Equal(2, StreamService.SelectPreferredAudioIndex(langs, "rus,ukr"));
        Assert.Equal(1, StreamService.SelectPreferredAudioIndex(langs, "ukr,rus"));
        Assert.Equal(0, StreamService.SelectPreferredAudioIndex(langs, "eng,ukr"));
    }

    [Fact]
    public void SelectPreferredAudioIndex_MapsTwoLetterAndFullNames()
    {
        Assert.Equal(0, StreamService.SelectPreferredAudioIndex(new List<string?> { "ru" }, "rus"));
        Assert.Equal(0, StreamService.SelectPreferredAudioIndex(new List<string?> { "russian" }, "ru"));
        Assert.Equal(0, StreamService.SelectPreferredAudioIndex(new List<string?> { "uk" }, "ua"));
        Assert.Equal(0, StreamService.SelectPreferredAudioIndex(new List<string?> { "en-US" }, "eng"));
    }

    [Fact]
    public void SelectPreferredAudioIndex_NoMatch_ReturnsMinusOne()
    {
        var langs = new List<string?> { "ukr", "eng" };
        Assert.Equal(-1, StreamService.SelectPreferredAudioIndex(langs, "rus"));
    }

    [Fact]
    public void SelectPreferredAudioIndex_MissingOrUndefinedMetadata_ReturnsMinusOne()
    {
        var langs = new List<string?> { null, "und", "" };
        Assert.Equal(-1, StreamService.SelectPreferredAudioIndex(langs, "rus"));
    }

    [Fact]
    public void SelectPreferredAudioIndex_EmptyPreference_ReturnsMinusOne()
    {
        var langs = new List<string?> { "rus" };
        Assert.Equal(-1, StreamService.SelectPreferredAudioIndex(langs, ""));
        Assert.Equal(-1, StreamService.SelectPreferredAudioIndex(langs, "  "));
    }

    [Fact]
    public void SelectPreferredAudioIndex_SkipsInvalidTracks()
    {
        var langs = new List<string?> { "rus", "rus", "eng" };
        var invalid = new HashSet<int> { 0, 1 };
        Assert.Equal(-1, StreamService.SelectPreferredAudioIndex(langs, "rus", invalid));
        Assert.Equal(2, StreamService.SelectPreferredAudioIndex(langs, "eng", invalid));

        var langs2 = new List<string?> { "rus", "rus" };
        var invalid2 = new HashSet<int> { 0 };
        Assert.Equal(1, StreamService.SelectPreferredAudioIndex(langs2, "rus", invalid2));
    }
}
