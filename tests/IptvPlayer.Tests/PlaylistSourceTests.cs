using IptvPlayer.Models;

namespace IptvPlayer.Tests;

public class PlaylistSourceTests
{
    [Theory]
    [InlineData("m3u", false, false)]
    [InlineData("m3u-vod", false, true)]
    [InlineData("portal", true, false)]
    [InlineData("M3U-VOD", false, true)]
    public void PlaylistSource_TypeFlags_FollowType(string type, bool isPortal, bool isVodCatalog)
    {
        var playlist = new PlaylistSource { Type = type };

        Assert.Equal(isPortal, playlist.IsPortal);
        Assert.Equal(isVodCatalog, playlist.IsVodCatalog);
    }

    [Fact]
    public void GetActiveEpgSources_VodCatalogWithoutOwnSources_SkipsGlobalFallback()
    {
        var settings = new AppSettings
        {
            EpgSources = [new EPGSource { Url = "http://epg.example.com/xmltv.xml" }],
            ActivePlaylistId = 1,
            Playlists = [new PlaylistSource { Id = 1, Type = "m3u-vod" }]
        };

        Assert.Empty(settings.GetActiveEpgSources());
    }

    [Fact]
    public void GetActiveEpgSources_VodCatalogWithOwnSources_UsesThem()
    {
        var source = new EPGSource { Url = "http://epg.example.com/xmltv.xml" };
        var settings = new AppSettings
        {
            EpgSources = [source],
            ActivePlaylistId = 1,
            Playlists = [new PlaylistSource { Id = 1, Type = "m3u-vod", EpgSources = [source] }]
        };

        var active = settings.GetActiveEpgSources();
        Assert.Single(active);
        Assert.Equal(source.Url, active[0].Url);
    }

    [Fact]
    public void GetActiveEpgSources_RegularPlaylist_FallsBackToGlobal()
    {
        var source = new EPGSource { Url = "http://epg.example.com/xmltv.xml" };
        var settings = new AppSettings
        {
            EpgSources = [source],
            ActivePlaylistId = 1,
            Playlists = [new PlaylistSource { Id = 1, Type = "m3u" }]
        };

        var active = settings.GetActiveEpgSources();
        Assert.Single(active);
        Assert.Equal(source.Url, active[0].Url);
    }
}

public class PlaylistCacheSourceChangeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".m3u");

    public void Dispose()
    {
        File.Delete(_path);
    }

    private PlaylistCache Cached() => new();

    [Fact]
    public void UpdateSourceState_ThenNoChange_ReturnsFalse()
    {
        File.WriteAllText(_path, "#EXTM3U\n");
        var cache = Cached();
        cache.UpdateSourceState(_path);

        Assert.False(cache.IsSourceChanged(_path));
    }

    [Fact]
    public void ContentChanged_ReturnsTrue()
    {
        File.WriteAllText(_path, "#EXTM3U\n#EXTINF:-1,A\nhttp://a/1.mp4\n");
        var cache = Cached();
        cache.UpdateSourceState(_path);

        File.WriteAllText(_path, "#EXTM3U\n#EXTINF:-1,B\nhttp://b/2.mp4\n");
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddMinutes(1));

        Assert.True(cache.IsSourceChanged(_path));
    }

    [Fact]
    public void TouchWithSameContent_ReturnsFalse()
    {
        File.WriteAllText(_path, "#EXTM3U\n");
        var cache = Cached();
        cache.UpdateSourceState(_path);

        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddMinutes(1));

        Assert.False(cache.IsSourceChanged(_path));
    }

    [Fact]
    public void NoSnapshotOrMissingFile_ReturnsFalse()
    {
        var cache = Cached();
        Assert.False(cache.IsSourceChanged(_path));

        cache.UpdateSourceState(_path);
        Assert.False(cache.IsSourceChanged(_path));
    }
}
