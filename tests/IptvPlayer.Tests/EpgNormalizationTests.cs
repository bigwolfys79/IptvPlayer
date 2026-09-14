using IptvPlayer.Services;

namespace IptvPlayer.Tests;


public class EpgNormalizationTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizeChannelName_EmptyInput_ReturnsEmpty(string? input, string expected)
    {
        Assert.Equal(expected, EpgNameNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("РБК HD", "рбк")]
    [InlineData("РБК FHD", "рбк")]
    [InlineData("РБК 4K", "рбк")]
    [InlineData("НТВ+2", "нтв")]
    [InlineData("НТВ +4", "нтв")]
    [InlineData("France 24 FR", "france 24")]
    [InlineData("Матч ТВ", "матч тв")]
    [InlineData("Ё-канал", "е канал")]
    [InlineData("РБК (Россия)", "рбк")]
    [InlineData("РБК  HD ", "рбк")]
    [InlineData("Discovery Channel", "discovery channel")]
    public void NormalizeChannelName_RemovesProviderNoise(string input, string expected)
    {
        Assert.Equal(expected, EpgNameNormalizer.Normalize(input));
    }

    [Fact]
    public void NormalizeChannelName_DifferentSpellings_ConvergeToSameKey()
    {


        Assert.Equal(
            EpgNameNormalizer.Normalize("Первый канал HD"),
            EpgNameNormalizer.Normalize("Первый канал"));
    }

    [Theory]
    [InlineData("360", "360")]
    [InlineData("BBC", "bbc")]
    public void NormalizeChannelName_SingleTokenName_IsNotStripped(string input, string expected)
    {
        Assert.Equal(expected, EpgNameNormalizer.Normalize(input));
    }

    [Fact]
    public void NormalizeChannelNamePreservingTimeshift_KeepsTimeshiftSuffix()
    {


        Assert.NotEqual(
            EpgNameNormalizer.NormalizePreservingTimeshift("НТВ +2"),
            EpgNameNormalizer.NormalizePreservingTimeshift("НТВ"));
    }
}
