using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.Services.OnlineCinema;

namespace IptvPlayer.Tests;

public class OnlineCinemaPlaylistSyncTests
{
    [Fact]
    public void Enabled_NoPlaylists_AddsCinemaSourceAndActivatesIt()
    {
        var settings = new AppSettings { OnlineCinemaEnabled = true };

        var changed = OnlineCinemaPlaylistSync.Sync(settings);

        Assert.True(changed);
        var cinema = Assert.Single(settings.Playlists);
        Assert.True(cinema.IsOnlineCinema);
        Assert.Equal(KinogoSite.BaseUrl, cinema.Url);
        Assert.Equal(cinema.Id, settings.ActivePlaylistId);
    }

    [Fact]
    public void Enabled_WithOtherPlaylists_AppendsWithoutTouchingActive()
    {
        var settings = new AppSettings
        {
            OnlineCinemaEnabled = true,
            ActivePlaylistId = 2,
            Playlists =
            {
                new PlaylistSource { Id = 2, Name = "ilock", Type = "m3u" }
            }
        };

        var changed = OnlineCinemaPlaylistSync.Sync(settings);

        Assert.True(changed);
        Assert.Equal(2, settings.Playlists.Count);
        Assert.Equal(2, settings.ActivePlaylistId);
        Assert.Contains(settings.Playlists, p => p.IsOnlineCinema);
    }

    [Fact]
    public void Enabled_AlreadySynced_ReturnsFalse()
    {
        var settings = new AppSettings { OnlineCinemaEnabled = true };
        OnlineCinemaPlaylistSync.Sync(settings);

        var changed = OnlineCinemaPlaylistSync.Sync(settings);

        Assert.False(changed);
        Assert.Single(settings.Playlists);
    }

    [Fact]
    public void Enabled_ManualCinemaSourceIsKept_NoDuplicate()
    {
        var settings = new AppSettings
        {
            OnlineCinemaEnabled = true,
            Playlists = { new PlaylistSource { Id = 5, Name = "kinogo.online", Type = "online-cinema" } }
        };

        var changed = OnlineCinemaPlaylistSync.Sync(settings);

        Assert.False(changed);
        Assert.Single(settings.Playlists);
    }

    [Fact]
    public void Disabled_RemovesCinemaSource_KeepsOthersActive()
    {
        var settings = new AppSettings
        {
            OnlineCinemaEnabled = false,
            ActivePlaylistId = 1,
            Playlists =
            {
                new PlaylistSource { Id = 1, Name = "Онлайн кинотеатр", Type = "online-cinema" },
                new PlaylistSource { Id = 2, Name = "ilock", Type = "m3u" }
            }
        };

        var changed = OnlineCinemaPlaylistSync.Sync(settings);

        Assert.True(changed);
        Assert.Equal(2, settings.ActivePlaylistId);
        var remaining = Assert.Single(settings.Playlists);
        Assert.Equal("ilock", remaining.Name);
    }

    [Fact]
    public void Disabled_LastSourceRemoved_ActiveResetsToZero()
    {
        var settings = new AppSettings
        {
            OnlineCinemaEnabled = false,
            ActivePlaylistId = 1,
            Playlists = { new PlaylistSource { Id = 1, Name = "Онлайн кинотеатр", Type = "online-cinema" } }
        };

        var changed = OnlineCinemaPlaylistSync.Sync(settings);

        Assert.True(changed);
        Assert.Empty(settings.Playlists);
        Assert.Equal(0, settings.ActivePlaylistId);
    }

    [Fact]
    public void Disabled_NoCinemaSource_ReturnsFalse()
    {
        var settings = new AppSettings
        {
            OnlineCinemaEnabled = false,
            Playlists = { new PlaylistSource { Id = 1, Name = "ilock", Type = "m3u" } }
        };

        var changed = OnlineCinemaPlaylistSync.Sync(settings);

        Assert.False(changed);
        Assert.Single(settings.Playlists);
    }
}
