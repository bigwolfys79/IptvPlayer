using System.Text;
using IptvPlayer.Services;

namespace IptvPlayer.Tests;

public class M3UParserServiceTests
{
    private static M3UParserService CreateParser() => new();

    [Fact]
    public void ParseContent_Empty_ReturnsNoChannels()
    {
        Assert.Empty(CreateParser().ParseContent(string.Empty));
        Assert.Empty(CreateParser().ParseContent(null!));
    }

    [Fact]
    public void ParseContent_BasicEntry_ParsesNameAndUrl()
    {
        var content = "#EXTM3U\n#EXTINF:-1,Первый канал\nhttp://example.com/1.m3u8\n";
        var channels = CreateParser().ParseContent(content);

        var ch = Assert.Single(channels);
        Assert.Equal("Первый канал", ch.Name);
        Assert.Equal("http://example.com/1.m3u8", ch.StreamUrl);
        Assert.Equal(1, ch.Id);
    }

    [Fact]
    public void ParseContent_WithAttributes_ParsesTvgIdLogoGroup()
    {
        var content = "#EXTM3U\n" +
            "#EXTINF:-1 tvg-id=\"first.ru\" tvg-logo=\"http://logo/1.png\" group-title=\"Федеральные\",Первый HD\n" +
            "http://example.com/1.m3u8\n";
        var channels = CreateParser().ParseContent(content);

        var ch = Assert.Single(channels);
        Assert.Equal("first.ru", ch.TvgId);
        Assert.Equal("http://logo/1.png", ch.LogoUrl);
        Assert.Equal("Федеральные", ch.Group);
    }

    [Theory]
    [InlineData("tvg-rec=\"7\"", 7)]
    [InlineData("catchup-days=\"3\"", 3)]
    [InlineData("catchup=\"default\"", 1)]
    public void ParseContent_ArchiveDepth_ReadsAllProviderVariants(string attr, int expectedDays)
    {
        var content = $"#EXTM3U\n#EXTINF:-1 {attr},Канал\nhttp://example.com/1.m3u8\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Equal(expectedDays, ch.CatchupDays);
    }

    [Fact]
    public void ParseContent_NoArchiveAttr_ZeroDays()
    {
        var content = "#EXTM3U\n#EXTINF:-1,Канал\nhttp://example.com/1.m3u8\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Equal(0, ch.CatchupDays);
    }

    [Fact]
    public void ParseContent_TvgRecTakesPrecedenceOverCatchupDays()
    {
        var content = "#EXTM3U\n#EXTINF:-1 tvg-rec=\"5\" catchup-days=\"9\",Канал\nhttp://example.com/1.m3u8\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Equal(5, ch.CatchupDays);
    }

    [Fact]
    public void ParseContent_ExtgrpAppliesToPreviousChannelWithoutGroup()
    {
        var content = "#EXTM3U\n#EXTINF:-1,Канал\nhttp://example.com/1.m3u8\n#EXTGRP:Музыка\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Equal("Музыка", ch.Group);
    }

    [Fact]
    public void ParseContent_ExtgrpBetweenExtinfAndUrl_AppliesToThatChannel()
    {


        var content = "#EXTM3U\n" +
            "#EXTINF:-1,Взрослый канал\n#EXTGRP:взрослые\nhttp://example.com/1.m3u8\n" +
            "#EXTINF:-1,Беларусь 24\n#EXTGRP:беларускія\nhttp://example.com/2.m3u8\n" +
            "#EXTINF:-1,Беларусь 1\n#EXTGRP:беларускія\nhttp://example.com/3.m3u8\n";
        var channels = CreateParser().ParseContent(content);

        Assert.Equal(3, channels.Count);
        Assert.Equal("взрослые", channels[0].Group);
        Assert.Equal("беларускія", channels[1].Group);
        Assert.Equal("http://example.com/2.m3u8", channels[1].StreamUrl);
        Assert.Equal("беларускія", channels[2].Group);
    }

    [Fact]
    public void ParseContent_ExtgrpBetweenExtinfAndUrl_DoesNotOverrideGroupTitle()
    {
        var content = "#EXTM3U\n" +
            "#EXTINF:-1 group-title=\"Кино\",Канал\n#EXTGRP:Музыка\nhttp://example.com/1.m3u8\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Equal("Кино", ch.Group);
    }

    [Fact]
    public void ParseContent_EntryWithoutUrl_IsSkipped()
    {
        var content = "#EXTM3U\n#EXTINF:-1,Канал без URL\n#EXTINF:-1,Канал с URL\nhttp://example.com/2.m3u8\n";
        var channels = CreateParser().ParseContent(content);
        var ch = Assert.Single(channels);
        Assert.Equal("Канал с URL", ch.Name);
    }

    [Fact]
    public void ParseContent_CommaInsideAttributes_NameTakenAfterLastComma()
    {
        var content = "#EXTM3U\n#EXTINF:-1 tvg-id=\"x, y\" group-title=\"A,B\",Канал\nhttp://example.com/1.m3u8\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Equal("Канал", ch.Name);
        Assert.Equal("x, y", ch.TvgId);
    }

    [Fact]
    public void ParseContent_EmptyName_GetsFallbackName()
    {
        var content = "#EXTM3U\n#EXTINF:-1,\nhttp://example.com/1.m3u8\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Equal("Канал 1", ch.Name);
    }

    [Fact]
    public void ParseContent_BomIsStripped()
    {
        var content = "\uFEFF#EXTM3U\n#EXTINF:-1,Канал\nhttp://example.com/1.m3u8\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Equal("Канал", ch.Name);
    }

    [Fact]
    public void ParseContent_UnquotedAttributeValue_IsRead()
    {
        var content = "#EXTM3U\n#EXTINF:-1 tvg-id=plain.id,Канал\nhttp://example.com/1.m3u8\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Equal("plain.id", ch.TvgId);
    }

    [Fact]
    public void ParseContent_DanglingEqualsAfterComma_DoesNotHang()
    {
        var content = "#EXTM3U\n#EXTINF:-1 tvg-id=\"1\",=x,Name\nhttp://example.com/1.m3u8\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Equal("Name", ch.Name);
        Assert.Equal("1", ch.TvgId);
    }

    [Fact]
    public void ParseContent_ExtinfWithoutDuration_IsParsed()
    {
        var content = "#EXTM3U\n#EXTINF:,Title\nhttp://example.com/1.m3u8\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Equal("Title", ch.Name);
        Assert.Equal("http://example.com/1.m3u8", ch.StreamUrl);
    }

    [Fact]
    public async Task ParseContent_Utf16LeBom_IsDecoded()
    {
        var text = "#EXTM3U\n#EXTINF:-1,Канал\nhttp://example.com/1.m3u8\n";
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text)).ToArray();
        var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".m3u");
        try
        {
            File.WriteAllBytes(tempFile, bytes);
            var channels = await CreateParser().ParseFromFileAsync(tempFile);
            var ch = Assert.Single(channels);
            Assert.Equal("Канал", ch.Name);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseContent_TvgYear_IsParsed()
    {
        var content = "#EXTM3U\n#EXTINF:-1 tvg-year=\"2025\",Фильм\nhttp://example.com/1.mp4\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Equal(2025, ch.Year);
    }

    [Fact]
    public void ParseContent_InvalidTvgYear_IsZero()
    {
        var content = "#EXTM3U\n#EXTINF:-1 tvg-year=\"abc\",Фильм\nhttp://example.com/1.mp4\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Equal(0, ch.Year);
    }

    [Fact]
    public void ParseContent_TvgGenre_KeepsAllTokens()
    {
        var content = "#EXTM3U\n#EXTINF:-1 tvg-genre=\"Комедия, Драма\",Фильм\nhttp://example.com/1.mp4\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Equal("Комедия, Драма", ch.Genre);
    }

    [Fact]
    public void ParseContent_ExtDescBetweenExtinfAndUrl_FillsDescription()
    {
        var content = "#EXTM3U\n" +
            "#EXTINF:-1 tvg-year=\"2024\" group-title=\"Кинотеатр / все фильмы / Фильмы / Драма\",Фильм (2024)\n" +
            "#EXTDESC:Описание фильма. [страница: https://kinogo.online/filmy/1.html]\n" +
            "http://example.com/1.mp4\n";
        var ch = Assert.Single(CreateParser().ParseContent(content, deriveGenreFromGroup: true));
        Assert.Equal("Описание фильма.", ch.Description);
        Assert.DoesNotContain("страница", ch.Description);
        Assert.Equal(2024, ch.Year);
    }

    [Fact]
    public void ParseContent_ExtDescWithoutPageMarker_IsKeptAsIs()
    {
        var content = "#EXTM3U\n#EXTINF:-1,Фильм\n#EXTDESC:Простое описание\nhttp://example.com/1.mp4\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Equal("Простое описание", ch.Description);
    }

    [Fact]
    public void ParseContent_ExtDescAfterUrl_IsIgnored()
    {
        var content = "#EXTM3U\n#EXTINF:-1,Фильм\nhttp://example.com/1.mp4\n#EXTDESC:Позднее описание\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Null(ch.Description);
    }

    [Fact]
    public void ParseContent_DeriveGenreFromGroup_SkipsPrefix_KeepsAllSegments()
    {
        var content = "#EXTM3U\n" +
            "#EXTINF:-1 group-title=\"Кинотеатр / все фильмы / Фильмы / Новинки / Комедия / Мелодрама\",Фильм\n" +
            "http://example.com/1.mp4\n";
        var ch = Assert.Single(CreateParser().ParseContent(content, deriveGenreFromGroup: true));
        Assert.Equal("Новинки, Комедия, Мелодрама", ch.Genre);
    }

    [Fact]
    public void ParseContent_DeriveGenreFromGroup_MultiGenreItem_InEachGenre()
    {
        var content = "#EXTM3U\n" +
            "#EXTINF:-1 group-title=\"Кинотеатр / все фильмы / Фильмы / Драма / Комедия / Мелодрама\",Фильм\n" +
            "http://example.com/1.mp4\n";
        var ch = Assert.Single(CreateParser().ParseContent(content, deriveGenreFromGroup: true));
        var genres = ch.Genre!.Split(", ");
        Assert.Equal(new[] { "Драма", "Комедия", "Мелодрама" }, genres);
    }

    [Fact]
    public void ParseContent_DeriveGenreFromGroup_NonGenreSegments_Dropped()
    {
        var content = "#EXTM3U\n" +
            "#EXTINF:-1 group-title=\"Кинотеатр / все сериалы / Сериалы / Сверхъестественное / Сезон 1\",Серия\n" +
            "http://example.com/1.mp4\n";
        var ch = Assert.Single(CreateParser().ParseContent(content, deriveGenreFromGroup: true));
        Assert.Null(ch.Genre);
    }

    [Fact]
    public void ParseContent_DeriveGenreFromGroup_SingleSegment_UsesGroup()
    {
        var content = "#EXTM3U\n#EXTINF:-1 group-title=\"Комедия\",Фильм\nhttp://example.com/1.mp4\n";
        var ch = Assert.Single(CreateParser().ParseContent(content, deriveGenreFromGroup: true));
        Assert.Equal("Комедия", ch.Genre);
    }

    [Fact]
    public void ParseContent_DeriveGenreDisabled_GenreStaysNull()
    {
        var content = "#EXTM3U\n" +
            "#EXTINF:-1 group-title=\"Кинотеатр / все фильмы / Фильмы / Драма\",Фильм\n" +
            "http://example.com/1.mp4\n";
        var ch = Assert.Single(CreateParser().ParseContent(content));
        Assert.Null(ch.Genre);
    }

    [Fact]
    public void ParseContent_TvgGenreTakesPrecedenceOverGroupDerivation()
    {
        var content = "#EXTM3U\n" +
            "#EXTINF:-1 tvg-genre=\"Боевик\" group-title=\"Кинотеатр / Фильмы / Драма\",Фильм\n" +
            "http://example.com/1.mp4\n";
        var ch = Assert.Single(CreateParser().ParseContent(content, deriveGenreFromGroup: true));
        Assert.Equal("Боевик", ch.Genre);
    }
}
