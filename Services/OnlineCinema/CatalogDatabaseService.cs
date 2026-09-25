using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;

public interface ICatalogDatabaseService
{
    Task UpsertItemsAsync(string siteId, IReadOnlyList<OnlineCinemaItem> items);

    Task SavePageInfoAsync(string siteId, OnlineCinemaPageInfo page);

    Task<List<OnlineCinemaPageInfo>> GetLoadedPagesAsync(string siteId);

    Task<int> GetTotalPagesAsync(string siteId, string category);

    Task<List<OnlineCinemaItem>> GetItemsAsync(string siteId, string? category = null);

    Task<int> CountItemsAsync(string siteId);
}


// SQLite storage for the online-cinema catalog in a separate DB file:
// the catalog is large (posters/descriptions) and must not bloat the playlist cache
public class CatalogDatabaseService : ICatalogDatabaseService
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer");

    private static readonly string DbPath = Path.Combine(CacheDirectory, "online_cinema.db");

    private readonly string _dbPath;
    private readonly ILogger<CatalogDatabaseService> _logger;
    private readonly object _initGate = new();
    private Task? _initTask;

    public CatalogDatabaseService(ILogger<CatalogDatabaseService> logger, string? dbPath = null)
    {
        _logger = logger;
        _dbPath = dbPath ?? DbPath;
    }

    // Lazy async schema init — keeps constructor off the UI thread
    private Task InitializeAsync()
    {
        lock (_initGate)
        {
            return _initTask ??= Task.Run(InitializeDatabase);
        }
    }

    private void InitializeDatabase()
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS catalog_items (
                    site_id TEXT NOT NULL,
                    page_url TEXT NOT NULL,
                    title TEXT NOT NULL,
                    year INTEGER NOT NULL DEFAULT 0,
                    poster_url TEXT,
                    genres TEXT,
                    description TEXT,
                    category TEXT,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY (site_id, page_url)
                );
                CREATE INDEX IF NOT EXISTS idx_catalog_items_site_cat ON catalog_items(site_id, category);
                CREATE TABLE IF NOT EXISTS catalog_pages (
                    site_id TEXT NOT NULL,
                    category TEXT NOT NULL,
                    page_number INTEGER NOT NULL,
                    total_pages INTEGER NOT NULL DEFAULT 0,
                    loaded_at_utc TEXT NOT NULL,
                    PRIMARY KEY (site_id, category, page_number)
                );";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось инициализировать БД каталога online_cinema.db.");
            throw;
        }
    }

    public async Task UpsertItemsAsync(string siteId, IReadOnlyList<OnlineCinemaItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        await InitializeAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var now = DateTime.UtcNow.ToString("o");
            foreach (var group in items.GroupBy(i => i.Category))
            {
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = (SqliteTransaction)transaction;
                cmd.CommandText = @"
                    INSERT INTO catalog_items (site_id, page_url, title, year, poster_url, genres, description, category, updated_at_utc)
                    VALUES ($site, $url, $title, $year, $poster, $genres, $descr, $cat, $now)
                    ON CONFLICT (site_id, page_url) DO UPDATE SET
                        title = excluded.title,
                        year = excluded.year,
                        poster_url = excluded.poster_url,
                        genres = excluded.genres,
                        description = excluded.description,
                        category = excluded.category,
                        updated_at_utc = excluded.updated_at_utc";
                var pSite = cmd.CreateParameter();
                pSite.ParameterName = "$site";
                pSite.Value = siteId;
                var pUrl = cmd.CreateParameter();
                pUrl.ParameterName = "$url";
                var pTitle = cmd.CreateParameter();
                pTitle.ParameterName = "$title";
                var pYear = cmd.CreateParameter();
                pYear.ParameterName = "$year";
                var pPoster = cmd.CreateParameter();
                pPoster.ParameterName = "$poster";
                var pGenres = cmd.CreateParameter();
                pGenres.ParameterName = "$genres";
                var pDescr = cmd.CreateParameter();
                pDescr.ParameterName = "$descr";
                var pCat = cmd.CreateParameter();
                pCat.ParameterName = "$cat";
                var pNow = cmd.CreateParameter();
                pNow.ParameterName = "$now";
                pNow.Value = now;
                cmd.Parameters.AddRange(new[] { pSite, pUrl, pTitle, pYear, pPoster, pGenres, pDescr, pCat, pNow });

                foreach (var item in group)
                {
                    pUrl.Value = item.PageUrl;
                    pTitle.Value = item.Title;
                    pYear.Value = item.Year;
                    pPoster.Value = item.PosterUrl;
                    pGenres.Value = string.Join(",", item.Genres);
                    pDescr.Value = item.Description;
                    pCat.Value = group.Key;
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить элементы каталога ({Count} шт.).", items.Count);
            throw;
        }
    }

    public async Task SavePageInfoAsync(string siteId, OnlineCinemaPageInfo page)
    {
        await InitializeAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO catalog_pages (site_id, category, page_number, total_pages, loaded_at_utc)
                VALUES ($site, $cat, $page, $total, $now)
                ON CONFLICT (site_id, category, page_number) DO UPDATE SET
                    total_pages = MAX(total_pages, excluded.total_pages),
                    loaded_at_utc = excluded.loaded_at_utc";
            cmd.Parameters.AddWithValue("$site", siteId);
            cmd.Parameters.AddWithValue("$cat", page.Category);
            cmd.Parameters.AddWithValue("$page", page.PageNumber);
            cmd.Parameters.AddWithValue("$total", page.TotalPages);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить страницу каталога {Category}/{Page}.", page.Category, page.PageNumber);
            throw;
        }
    }

    public async Task<List<OnlineCinemaPageInfo>> GetLoadedPagesAsync(string siteId)
    {
        await InitializeAsync();
        var result = new List<OnlineCinemaPageInfo>();
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT category, page_number, total_pages, loaded_at_utc FROM catalog_pages WHERE site_id = $site";
        cmd.Parameters.AddWithValue("$site", siteId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new OnlineCinemaPageInfo
            {
                Category = reader.GetString(0),
                PageNumber = reader.GetInt32(1),
                TotalPages = reader.GetInt32(2),
                LoadedAtUtc = DateTime.TryParse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts) ? ts : DateTime.MinValue
            });
        }

        return result;
    }

    public async Task<int> GetTotalPagesAsync(string siteId, string category)
    {
        await InitializeAsync();
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(total_pages), 0) FROM catalog_pages WHERE site_id = $site AND category = $cat";
        cmd.Parameters.AddWithValue("$site", siteId);
        cmd.Parameters.AddWithValue("$cat", category);
        var value = await cmd.ExecuteScalarAsync();
        return value is long v ? (int)v : 0;
    }

    public async Task<List<OnlineCinemaItem>> GetItemsAsync(string siteId, string? category = null)
    {
        await InitializeAsync();
        var result = new List<OnlineCinemaItem>();
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT page_url, title, year, poster_url, genres, description, category
            FROM catalog_items
            WHERE site_id = $site AND ($cat IS NULL OR category = $cat)";
        cmd.Parameters.AddWithValue("$site", siteId);
        cmd.Parameters.AddWithValue("$cat", (object?)category ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new OnlineCinemaItem
            {
                PageUrl = reader.GetString(0),
                Title = reader.GetString(1),
                Year = reader.GetInt32(2),
                PosterUrl = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Genres = reader.IsDBNull(4) || reader.GetString(4).Length == 0
                    ? new List<string>()
                    : reader.GetString(4).Split(',').Select(g => g.Trim()).Where(g => g.Length > 0).ToList(),
                Description = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                Category = reader.IsDBNull(6) ? string.Empty : reader.GetString(6)
            });
        }

        return result;
    }

    public async Task<int> CountItemsAsync(string siteId)
    {
        await InitializeAsync();
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM catalog_items WHERE site_id = $site";
        cmd.Parameters.AddWithValue("$site", siteId);
        var value = await cmd.ExecuteScalarAsync();
        return value is long v ? (int)v : 0;
    }
}
