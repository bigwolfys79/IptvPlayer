using IptvPlayer.Services;

namespace IptvPlayer.Tests;

/// <summary>
/// Тесты нормализации имён каналов — основа 4-уровневого сопоставления
/// плейлист ↔ XMLTV (см. EPGService.MatchChannel).
/// </summary>
public class EpgNormalizationTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizeChannelName_EmptyInput_ReturnsEmpty(string? input, string expected)
    {
        Assert.Equal(expected, EPGService.NormalizeChannelName(input));
    }

    [Theory]
    [InlineData("РБК HD", "рбк")]            // суффикс качества срезается
    [InlineData("РБК FHD", "рбк")]
    [InlineData("РБК 4K", "рбк")]
    [InlineData("НТВ+2", "нтв")]             // таймшифт-суффикс срезается
    [InlineData("НТВ +4", "нтв")]
    [InlineData("France 24 FR", "france 24")] // хвостовой код страны срезается
    [InlineData("Матч ТВ", "матч тв")]       // ё→е, регистр
    [InlineData("Ё-канал", "е канал")]
    [InlineData("РБК (Россия)", "рбк")]      // региональные скобки стираются
    [InlineData("РБК  HD ", "рбк")]          // лишние пробелы схлопываются
    [InlineData("Discovery Channel", "discovery channel")]
    public void NormalizeChannelName_RemovesProviderNoise(string input, string expected)
    {
        Assert.Equal(expected, EPGService.NormalizeChannelName(input));
    }

    [Fact]
    public void NormalizeChannelName_DifferentSpellings_ConvergeToSameKey()
    {
        // Один и тот же канал в M3U и в XMLTV пишется по-разному —
        // нормализация должна давать один ключ.
        Assert.Equal(
            EPGService.NormalizeChannelName("Первый канал HD"),
            EPGService.NormalizeChannelName("Первый канал"));
    }

    [Theory]
    [InlineData("360", "360")] // название из одного токена-маркера не режется до пустоты
    [InlineData("BBC", "bbc")]
    public void NormalizeChannelName_SingleTokenName_IsNotStripped(string input, string expected)
    {
        Assert.Equal(expected, EPGService.NormalizeChannelName(input));
    }

    [Fact]
    public void NormalizeChannelNamePreservingTimeshift_KeepsTimeshiftSuffix()
    {
        // Строгий ключ карты имён: таймшифт-версии получают собственное
        // расписание, поэтому "+2" должен отличаться от базового канала.
        Assert.NotEqual(
            EPGService.NormalizeChannelNamePreservingTimeshift("НТВ +2"),
            EPGService.NormalizeChannelNamePreservingTimeshift("НТВ"));
    }
}
