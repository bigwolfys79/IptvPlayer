using System;
using System.Collections.Generic;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace IptvPlayer.Tests;


public class EpgDeltaReuseTests
{
    private static readonly DateTime Base = new(2026, 9, 23, 10, 0, 0);

    private static EPGEntry Entry(
        string channelId,
        string channelName,
        string programName,
        int startOffsetHours,
        double durationHours = 1,
        string? description = null,
        string? category = null) =>
        new()
        {
            EventId = $"{channelId}_{startOffsetHours}",
            ChannelId = channelId,
            ChannelName = channelName,
            ProgramName = programName,
            Description = description,
            Category = category,
            StartTime = Base.AddHours(startOffsetHours),
            EndTime = Base.AddHours(startOffsetHours + durationHours)
        };

    private static Dictionary<string, List<EPGEntry>> Dict(
        params (string Id, List<EPGEntry> Entries)[] channels)
    {
        var dict = new Dictionary<string, List<EPGEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entries) in channels)
        {
            dict[id] = entries;
        }

        return dict;
    }

    [Fact]
    public void ApplyDeltaReuse_UnchangedProgramme_KeepsPreviousInstance()
    {
        var old = Entry("ch1", "Первый", "Новости", 0, description: "desc", category: "news");
        var previous = Dict(("ch1", new List<EPGEntry> { old }));
        var current = Dict(("ch1", new List<EPGEntry>
        {
            Entry("ch1", "Первый", "Новости", 0, description: "desc", category: "news")
        }));

        var reused = EpgSourceMerger.ApplyDeltaReuse(previous, current, NullLogger.Instance);

        Assert.Equal(1, reused);
        Assert.Same(old, current["ch1"][0]);
    }

    [Fact]
    public void ApplyDeltaReuse_ChangedDescription_CreatesNewInstance()
    {
        var old = Entry("ch1", "Первый", "Новости", 0, description: "old text");
        var previous = Dict(("ch1", new List<EPGEntry> { old }));
        var current = Dict(("ch1", new List<EPGEntry>
        {
            Entry("ch1", "Первый", "Новости", 0, description: "new text")
        }));

        var reused = EpgSourceMerger.ApplyDeltaReuse(previous, current, NullLogger.Instance);

        Assert.Equal(0, reused);
        Assert.NotSame(old, current["ch1"][0]);
    }

    [Fact]
    public void ApplyDeltaReuse_ChangedEndTime_CreatesNewInstance()
    {
        var old = Entry("ch1", "Первый", "Новости", 0);
        var previous = Dict(("ch1", new List<EPGEntry> { old }));
        var current = Dict(("ch1", new List<EPGEntry> { Entry("ch1", "Первый", "Новости", 0, 2) }));

        var reused = EpgSourceMerger.ApplyDeltaReuse(previous, current, NullLogger.Instance);

        Assert.Equal(0, reused);
        Assert.NotSame(old, current["ch1"][0]);
    }

    [Fact]
    public void ApplyDeltaReuse_ChangedChannelName_CreatesNewInstance()
    {
        var old = Entry("ch1", "Первый", "Новости", 0);
        var previous = Dict(("ch1", new List<EPGEntry> { old }));
        var current = Dict(("ch1", new List<EPGEntry> { Entry("ch1", "Первый канал", "Новости", 0) }));

        var reused = EpgSourceMerger.ApplyDeltaReuse(previous, current, NullLogger.Instance);

        Assert.Equal(0, reused);
        Assert.NotSame(old, current["ch1"][0]);
    }

    [Fact]
    public void ApplyDeltaReuse_RemovedAndAddedEntries_Handled()
    {
        var kept = Entry("ch1", "Первый", "Фильм", 1);
        var previous = Dict(("ch1", new List<EPGEntry>
        {
            Entry("ch1", "Первый", "Новости", 0),
            kept
        }));
        var current = Dict(("ch1", new List<EPGEntry>
        {
            Entry("ch1", "Первый", "Фильм", 1),
            Entry("ch1", "Первый", "Сериал", 2)
        }));

        var reused = EpgSourceMerger.ApplyDeltaReuse(previous, current, NullLogger.Instance);

        Assert.Equal(1, reused);
        Assert.Same(kept, current["ch1"][0]);
        Assert.Equal("Сериал", current["ch1"][1].ProgramName);
    }

    [Fact]
    public void ApplyDeltaReuse_ChannelGoneFromCurrent_NotResurrected()
    {
        var previous = Dict(
            ("ch1", new List<EPGEntry> { Entry("ch1", "Первый", "Новости", 0) }),
            ("ch2", new List<EPGEntry> { Entry("ch2", "НТВ", "Сегодня", 0) }));
        var current = Dict(("ch1", new List<EPGEntry> { Entry("ch1", "Первый", "Новости", 0) }));

        var reused = EpgSourceMerger.ApplyDeltaReuse(previous, current, NullLogger.Instance);

        Assert.Equal(1, reused);
        Assert.False(current.ContainsKey("ch2"));
    }

    [Fact]
    public void ApplyDeltaReuse_NullOrEmptyPrevious_IsNoOp()
    {
        var current = Dict(("ch1", new List<EPGEntry> { Entry("ch1", "Первый", "Новости", 0) }));

        Assert.Equal(0, EpgSourceMerger.ApplyDeltaReuse(null, current, NullLogger.Instance));
        Assert.Equal(0, EpgSourceMerger.ApplyDeltaReuse(
            new Dictionary<string, List<EPGEntry>>(StringComparer.OrdinalIgnoreCase),
            current, NullLogger.Instance));
    }

    [Fact]
    public void ApplyDeltaReuse_DuplicatePreviousKey_KeepsFirstWithoutThrow()
    {
        var first = Entry("ch1", "Первый", "А", 0);
        var previous = Dict(("ch1", new List<EPGEntry>
        {
            first,
            Entry("ch1", "Первый", "Б", 0, 2)
        }));
        var current = Dict(("ch1", new List<EPGEntry> { Entry("ch1", "Первый", "А", 0) }));

        var reused = EpgSourceMerger.ApplyDeltaReuse(previous, current, NullLogger.Instance);

        Assert.Equal(1, reused);
        Assert.Same(first, current["ch1"][0]);
    }

    [Fact]
    public void ApplyDeltaReuse_ChannelIdCaseDifference_StillMatches()
    {
        var old = Entry("ch1", "Первый", "Новости", 0);
        var previous = Dict(("ch1", new List<EPGEntry> { old }));
        var current = Dict(("CH1", new List<EPGEntry> { Entry("ch1", "Первый", "Новости", 0) }));

        var reused = EpgSourceMerger.ApplyDeltaReuse(previous, current, NullLogger.Instance);

        Assert.Equal(1, reused);
        Assert.Same(old, current["CH1"][0]);
    }

    [Fact]
    public void Merge_WithPrevious_ReusesUnchangedInstances()
    {
        var old = Entry("ch1", "Первый", "Новости", 0, description: "desc");
        var previous = Dict(("ch1", new List<EPGEntry> { old }));

        var result = new XmlTvLoadResult
        {
            Entries = new List<EPGEntry>
            {
                Entry("ch1", "Первый", "Новости", 0, description: "desc"),
                Entry("ch1", "Первый", "Фильм", 1)
            },
            ChannelIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        var (byChannel, _, nameIndex, _) = EpgSourceMerger.Merge(
            new List<XmlTvLoadResult> { result }, NullLogger.Instance, previous);

        Assert.Same(old, byChannel["ch1"][0]);
        Assert.Same(byChannel["ch1"], nameIndex["первый"]);
    }
}
