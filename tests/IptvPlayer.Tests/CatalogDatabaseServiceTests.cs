using IptvPlayer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace IptvPlayer.Tests;

public class CatalogDatabaseServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly CatalogDatabaseService _db;

    public CatalogDatabaseServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cinema_test_{Guid.NewGuid():N}.db");
        _db = new CatalogDatabaseService(NullLogger<CatalogDatabaseService>.Instance, _dbPath);
    }

    public void Dispose()
    {
        // SQLite connection pool holds the file open — drop it before cleanup
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private static OnlineCinemaItem Item(string url, string category = "все фильмы", int year = 2026) => new()
    {
        PageUrl = url,
        Title = "Фильм " + url,
        Year = year,
        PosterUrl = "https://example.com/" + url + ".webp",
        Genres = ["Драма", "Комедия"],
        Description = "Описание",
        Category = category
    };

    [Fact]
    public async Task UpsertThenGet_RoundTripsItems()
    {
        await _db.UpsertItemsAsync("kinogo", [Item("https://k/1.html")]);

        var items = await _db.GetItemsAsync("kinogo");

        var item = Assert.Single(items);
        Assert.Equal("https://k/1.html", item.PageUrl);
        Assert.Equal(2026, item.Year);
        Assert.Equal(["Драма", "Комедия"], item.Genres);
        Assert.Equal("все фильмы", item.Category);
    }

    [Fact]
    public async Task UpsertSamePageUrl_UpdatesInsteadOfDuplicating()
    {
        await _db.UpsertItemsAsync("kinogo", [Item("https://k/1.html", year: 2020)]);
        await _db.UpsertItemsAsync("kinogo", [Item("https://k/1.html", year: 2026)]);

        var items = await _db.GetItemsAsync("kinogo");

        var item = Assert.Single(items);
        Assert.Equal(2026, item.Year);
        Assert.Equal(1, await _db.CountItemsAsync("kinogo"));
    }

    [Fact]
    public async Task GetItems_CategoryFilter_ReturnsOnlyThatCategory()
    {
        await _db.UpsertItemsAsync("kinogo", [
            Item("https://k/a.html", category: "комедия"),
            Item("https://k/b.html", category: "драма")
        ]);

        var comedies = await _db.GetItemsAsync("kinogo", "комедия");

        var item = Assert.Single(comedies);
        Assert.Equal("https://k/a.html", item.PageUrl);
    }

    [Fact]
    public async Task PageInfo_TracksTotalPagesAcrossLoads()
    {
        await _db.SavePageInfoAsync("kinogo", new OnlineCinemaPageInfo
        { Category = "все фильмы", PageNumber = 1, TotalPages = 11 });
        await _db.SavePageInfoAsync("kinogo", new OnlineCinemaPageInfo
        { Category = "все фильмы", PageNumber = 3, TotalPages = 12 });

        var pages = await _db.GetLoadedPagesAsync("kinogo");

        Assert.Equal(2, pages.Count);
        Assert.Equal(12, await _db.GetTotalPagesAsync("kinogo", "все фильмы"));
        Assert.Equal(0, await _db.GetTotalPagesAsync("kinogo", "драма"));
        Assert.All(pages, p => Assert.True(p.LoadedAtUtc > DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public async Task PageInfo_SamePageReloaded_KeepsMaxTotalAndRefreshesTimestamp()
    {
        await _db.SavePageInfoAsync("kinogo", new OnlineCinemaPageInfo
        { Category = "драма", PageNumber = 1, TotalPages = 5 });
        await _db.SavePageInfoAsync("kinogo", new OnlineCinemaPageInfo
        { Category = "драма", PageNumber = 1, TotalPages = 7 });

        var pages = await _db.GetLoadedPagesAsync("kinogo");

        var page = Assert.Single(pages);
        Assert.Equal(7, page.TotalPages);
    }

    [Fact]
    public async Task Items_AreScopedBySite()
    {
        await _db.UpsertItemsAsync("kinogo", [Item("https://k/1.html")]);

        Assert.Equal(0, await _db.CountItemsAsync("other-site"));
        Assert.Empty(await _db.GetItemsAsync("other-site"));
    }
}
