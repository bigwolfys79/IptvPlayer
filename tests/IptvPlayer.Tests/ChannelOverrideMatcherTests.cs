using System;
using System.Collections.Generic;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Xunit;

namespace IptvPlayer.Tests;

public class ChannelOverrideMatcherTests
{
    private static PlaylistDatabaseService.ChannelOverride Move(
        string url, string name, string? tvgId, string newGroup) =>
        new(1, url, name, "Старая", tvgId, newGroup, IsDeleted: false, CreatedAtUtc: DateTime.UtcNow);

    private static PlaylistDatabaseService.ChannelOverride Delete(string url, string name) =>
        new(1, url, name, "Группа", null, null, IsDeleted: true, CreatedAtUtc: DateTime.UtcNow);

    private static ChannelViewModel Channel(int id, string name, string url, string? group, string? tvgId = null) =>
        new() { Id = id, Name = name, StreamUrl = url, Group = group, TvgId = tvgId };

    [Fact]
    public void Apply_EmptyOverrides_ReturnsAllChannels()
    {
        var channels = new List<ChannelViewModel> { Channel(1, "Первый", "http://a/1", "Новости") };

        var result = ChannelOverrideMatcher.Apply(channels, new List<PlaylistDatabaseService.ChannelOverride>());

        Assert.Single(result);
    }

    [Fact]
    public void Apply_DeletedByUrl_RemovesChannel()
    {
        var channels = new List<ChannelViewModel>
        {
            Channel(1, "Первый", "http://a/1", "Новости"),
            Channel(2, "Второй", "http://a/2", "Новости")
        };

        var result = ChannelOverrideMatcher.Apply(
            channels, new List<PlaylistDatabaseService.ChannelOverride> { Delete("http://a/1", "Первый") });

        Assert.Single(result);
        Assert.Equal("Второй", result[0].Name);
    }

    [Fact]
    public void Apply_MoveByUrl_ChangesGroup()
    {
        var channels = new List<ChannelViewModel> { Channel(1, "Первый", "http://a/1", "Новости") };

        var result = ChannelOverrideMatcher.Apply(
            channels, new List<PlaylistDatabaseService.ChannelOverride> { Move("http://a/1", "Первый", null, "Кино") });

        Assert.Equal("Кино", result[0].Group);
    }

    [Fact]
    public void Apply_MoveByTvgId_WhenUrlChanged()
    {
        var channels = new List<ChannelViewModel> { Channel(1, "Первый", "http://new-host/1", "Новости", tvgId: "ch1") };

        var result = ChannelOverrideMatcher.Apply(
            channels, new List<PlaylistDatabaseService.ChannelOverride> { Move("http://old-host/1", "Первый", "ch1", "Кино") });

        Assert.Equal("Кино", result[0].Group);
    }

    [Fact]
    public void Apply_MoveByNormalizedName_WhenUrlAndTvgIdChanged()
    {
        var channels = new List<ChannelViewModel> { Channel(1, "Первый  канал", "http://new-host/1", "Новости") };

        var result = ChannelOverrideMatcher.Apply(
            channels, new List<PlaylistDatabaseService.ChannelOverride> { Move("http://old-host/1", "Первый канал", null, "Кино") });

        Assert.Equal("Кино", result[0].Group);
    }

    [Fact]
    public void Apply_UnknownOverride_DoesNothing()
    {
        var channels = new List<ChannelViewModel> { Channel(1, "Первый", "http://a/1", "Новости") };

        var result = ChannelOverrideMatcher.Apply(
            channels, new List<PlaylistDatabaseService.ChannelOverride> { Move("http://other/9", "Другой", null, "Кино") });

        Assert.Equal("Новости", result[0].Group);
    }

    [Fact]
    public void IsPinRequiredForGroup_RequiresAllConditions()
    {
        var s = new AppSettings
        {
            ParentalControlEnabled = true,
            ParentalControlPinHash = "abc:def",
            ParentalControlBlockedGroups = new List<string> { "18+" }
        };

        Assert.True(ParentalControlService.IsPinRequiredForGroup(s, "18+"));
        Assert.False(ParentalControlService.IsPinRequiredForGroup(s, "Новости"));
        Assert.False(ParentalControlService.IsPinRequiredForGroup(s, null));

        s.ParentalControlUnlockedUntilUtc = DateTime.UtcNow.AddMinutes(10);
        Assert.False(ParentalControlService.IsPinRequiredForGroup(s, "18+"));

        s.ParentalControlUnlockedUntilUtc = null;
        s.ParentalControlPinHash = null;
        Assert.False(ParentalControlService.IsPinRequiredForGroup(s, "18+"));
    }
}
