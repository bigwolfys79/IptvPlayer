using System;
using System.Collections.Generic;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace IptvPlayer.Tests;


public class EpgTimeshiftTests
{
    [Theory]
    [InlineData("НТВ +2", 2, "НТВ")]
    [InlineData("Первый канал+3", 3, "Первый канал")]
    [InlineData("НТВ + 4", 4, "НТВ")]
    [InlineData("НТВ (+2)", 2, "НТВ")]
    [InlineData("НТВ +0", 0, "НТВ")]
    [InlineData("НТВ +12", 12, "НТВ")]
    [InlineData("НТВ HD +2", 2, "НТВ HD")]
    [InlineData("НТВ +2 HD", 2, "НТВ")]
    [InlineData("Первый канал +4 HD", 4, "Первый канал")]
    [InlineData("Мосфильм. Золотая коллекция +4 HD", 4, "Мосфильм. Золотая коллекция")]
    [InlineData("ITV4 +1 UK", 1, "ITV4")]
    [InlineData("Россия 1 +0 (Элиста)", 0, "Россия 1")]
    [InlineData("Первый канал +2 (Белорецк)", 2, "Первый канал")]
    [InlineData("Домашний +7 (Владивосток)", 7, "Домашний")]
    [InlineData("Карусель +3 (Омск)", 3, "Карусель")]
    public void TryGetTimeshiftHours_TrailingSuffix_Parses(string name, int expectedHours, string expectedBase)
    {
        Assert.True(EpgNameNormalizer.TryGetTimeshiftHours(name, out var hours, out var baseName));
        Assert.Equal(expectedHours, hours);
        Assert.Equal(expectedBase, baseName);
    }

    [Theory]
    [InlineData("НТВ")]
    [InlineData("НТВ +13")]
    [InlineData("Кино +100")]
    [InlineData("Canal+ 360 HD PL")]
    [InlineData("M+ Acción ES")]
    [InlineData("Канал ТВ+")]
    [InlineData("Travel+Adventure")]
    [InlineData("Иллюзион+")]
    [InlineData("")]
    [InlineData(null)]
    public void TryGetTimeshiftHours_NoTimeshiftSuffix_ReturnsFalse(string? name)
    {
        Assert.False(EpgNameNormalizer.TryGetTimeshiftHours(name, out _, out _));
    }

    [Fact]
    public void TryGetTimeshiftHours_BaseName_KeepsTimeshiftInNormalizePreserving()
    {
        EpgNameNormalizer.TryGetTimeshiftHours("НТВ +2", out _, out var baseName);
        Assert.Equal("нтв", EpgNameNormalizer.NormalizePreservingTimeshift(baseName));
        Assert.NotEqual("нтв", EpgNameNormalizer.NormalizePreservingTimeshift("НТВ +2"));
    }

    [Fact]
    public void CreateTimeshiftedEntries_ShiftsTimesAndClones()
    {
        var start = new DateTime(2026, 9, 23, 10, 0, 0);
        var source = new List<EPGEntry>
        {
            new()
            {
                EventId = "id1_20260923100000",
                ChannelId = "id1",
                ChannelName = "НТВ",
                ProgramName = "Сегодня",
                Description = "Новости",
                Category = "news",
                StartTime = start,
                EndTime = start.AddHours(1)
            }
        };

        var shifted = EPGService.CreateTimeshiftedEntries(source, 2);

        Assert.Single(shifted);
        Assert.NotSame(source[0], shifted[0]);
        Assert.Equal(start.AddHours(2), shifted[0].StartTime);
        Assert.Equal(start.AddHours(3), shifted[0].EndTime);
        Assert.Equal("Сегодня", shifted[0].ProgramName);
        Assert.Equal("Новости", shifted[0].Description);
        Assert.Equal("news", shifted[0].Category);
        Assert.Equal("id1", shifted[0].ChannelId);
        Assert.Equal("id1_20260923100000_ts2", shifted[0].EventId);
        Assert.Equal(start, source[0].StartTime);
    }

    [Fact]
    public void CreateTimeshiftedEntries_EmptySource_ReturnsEmpty()
    {
        Assert.Empty(EPGService.CreateTimeshiftedEntries(new List<EPGEntry>(), 2));
    }

    [Fact]
    public void BuildNameIndex_PreserveTimeshift_KeepsShiftedVariantsDistinct()
    {
        var start = new DateTime(2026, 9, 23, 10, 0, 0);
        var byChannel = new Dictionary<string, List<EPGEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            ["id_ntv"] = new()
            {
                new EPGEntry
                {
                    EventId = "a", ChannelId = "id_ntv", ChannelName = "НТВ",
                    ProgramName = "П", StartTime = start, EndTime = start.AddHours(1)
                }
            },
            ["id_ntv2"] = new()
            {
                new EPGEntry
                {
                    EventId = "b", ChannelId = "id_ntv2", ChannelName = "НТВ +2",
                    ProgramName = "П", StartTime = start, EndTime = start.AddHours(1)
                }
            }
        };

        var lenient = EpgSourceMerger.BuildNameIndex(byChannel, NullLogger.Instance);
        var preserved = EpgSourceMerger.BuildNameIndex(byChannel, NullLogger.Instance, preserveTimeshift: true);

        // Lenient index collapses both variants to one key
        Assert.Single(lenient);
        Assert.Equal("нтв", Assert.Single(lenient.Keys));
        // Preserved index keeps them distinct
        Assert.Equal(2, preserved.Count);
        Assert.Contains("нтв", preserved.Keys);
        var shiftedKey = EpgNameNormalizer.NormalizePreservingTimeshift("НТВ +2");
        Assert.NotEqual("нтв", shiftedKey);
        Assert.Same(byChannel["id_ntv2"], preserved[shiftedKey]);
    }

    [Fact]
    public void Merge_ReturnsBothNameIndexes()
    {
        var start = new DateTime(2026, 9, 23, 10, 0, 0);
        var result = new XmlTvLoadResult
        {
            Entries = new List<EPGEntry>
            {
                new()
                {
                    EventId = "a", ChannelId = "id_ntv", ChannelName = "НТВ",
                    ProgramName = "П", StartTime = start, EndTime = start.AddHours(1)
                }
            },
            ChannelIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        var (_, _, nameIndex, preservedIndex) = EpgSourceMerger.Merge(
            new List<XmlTvLoadResult> { result }, NullLogger.Instance);

        Assert.NotEmpty(nameIndex);
        Assert.Equal(nameIndex["нтв"], preservedIndex["нтв"]);
    }
}
