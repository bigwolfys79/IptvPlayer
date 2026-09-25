using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;

// Orchestrates catalog loading for the online cinema: first sync loads page 1 of
// every category, later opens refresh stale first pages in the background (new
// films appear on page 1), deeper pages load on demand as the user scrolls.
// All methods must be called on the UI thread (WebView2 requirement).
public class OnlineCinemaCatalogService
{
    private const int Page1RefreshHours = 6;
    private const int PauseBetweenPagesMs = 2000;

    private readonly ICatalogDatabaseService _db;
    private readonly OnlineCinemaBrowserService _browser;
    private readonly ILogger _logger;
    private readonly Random _random = new();

    public OnlineCinemaCatalogService(
        ICatalogDatabaseService db, OnlineCinemaBrowserService browser, ILogger<OnlineCinemaCatalogService> logger)
    {
        _db = db;
        _browser = browser;
        _logger = logger;
    }

    // Status text for the loading overlay / toast
    public event Action<string>? Progress;

    public static string YearCategoryKey(int year) => $"год {year}";

    public async Task<List<OnlineCinemaItem>> LoadCategoryPageAsync(string category, int page, CancellationToken ct = default)
    {
        if (!_browser.IsInitialized)
        {
            await _browser.InitializeAsync();
        }

        var url = KinogoSite.CategoryUrl(category, page);
        if (!await _browser.NavigateAsync(url, ct))
        {
            throw new InvalidOperationException($"Не удалось открыть страницу каталога: {url}");
        }

        var result = await _browser.RunScriptJsonAsync(KinogoSite.ParseCategoryPageJs, ct);
        var items = new List<OnlineCinemaItem>();
        if (result.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                var item = KinogoSite.ToItem(el, category);
                if (item != null)
                {
                    items.Add(item);
                }
            }
        }

        var total = result.TryGetProperty("total", out var t) && t.TryGetInt32(out var tp) ? tp : 0;
        await _db.UpsertItemsAsync(KinogoSite.Id, items);
        await _db.SavePageInfoAsync(KinogoSite.Id, new OnlineCinemaPageInfo
        {
            Category = category,
            PageNumber = page,
            TotalPages = total
        });

        Progress?.Invoke($"{category}: страница {page}{(total > 0 ? $" из {total}" : "")}");
        _logger.LogInformation("Онлайн-кинотеатр: {Category} стр. {Page} — {Count} карточек, всего страниц {Total}.",
            category, page, items.Count, total);
        return items;
    }

    // Called when the source opens: page 1 of every category is (re)loaded when
    // missing or stale, everything else comes from the DB — instant open
    public async Task RefreshFirstPagesAsync(CancellationToken ct = default)
    {
        if (!_browser.IsInitialized)
        {
            await _browser.InitializeAsync();
        }

        var loaded = await _db.GetLoadedPagesAsync(KinogoSite.Id);
        var staleThreshold = DateTime.UtcNow.AddHours(-Page1RefreshHours);
        foreach (var category in KinogoSite.Categories.Keys)
        {
            ct.ThrowIfCancellationRequested();
            var page1 = loaded.FirstOrDefault(p => p.Category == category && p.PageNumber == 1);
            if (page1 != null && page1.LoadedAtUtc > staleThreshold)
            {
                continue;
            }

            try
            {
                await LoadCategoryPageAsync(category, 1, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Онлайн-кинотеатр: не удалось обновить «{Category}».", category);
            }

            await Task.Delay(JitteredPause(), ct);
        }
    }

    // Loads the next unloaded page of a category; when total is unknown keeps
    // going while the previous page returned cards
    public async Task<List<OnlineCinemaItem>?> LoadNextPageAsync(string category, CancellationToken ct = default)
    {
        var pages = (await _db.GetLoadedPagesAsync(KinogoSite.Id))
            .Where(p => p.Category == category)
            .ToList();
        if (pages.Count == 0)
        {
            return await LoadCategoryPageAsync(category, 1, ct);
        }

        var next = pages.Max(p => p.PageNumber) + 1;
        var total = pages.Max(p => p.TotalPages);
        if (total > 0 && next > total)
        {
            return null;
        }

        try
        {
            return await LoadCategoryPageAsync(category, next, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Онлайн-кинотеатр: страница {Page} «{Category}» недоступна.", next, category);
            return null;
        }
    }

    // Site-side year selection: sets the session year filter (the first page of a
    // category is different per year), fetches its page 1 into a pseudo-category.
    // The session filter persists on the site side — subsequent page loads of any
    // category return that year's content, which is why the page marker is scoped.
    public async Task<List<OnlineCinemaItem>?> SyncYearAsync(int year, CancellationToken ct = default)
    {
        if (!_browser.IsInitialized)
        {
            await _browser.InitializeAsync();
        }

        var baseCategory = KinogoSite.Categories.Keys.First();
        var basePage = KinogoSite.CategoryUrl(baseCategory, 1);
        if (!await _browser.NavigateAsync(basePage, ct))
        {
            return null;
        }

        var js = KinogoSite.SetYearFilterJsTemplate.Replace("__YEAR__", year.ToString());
        await _browser.RunScriptJsonAsync(js, ct);
        await Task.Delay(1500, ct);

        return await LoadCategoryPageAsync(YearCategoryKey(year), 1, ct);
    }

    private int JitteredPause() => PauseBetweenPagesMs + _random.Next(0, 1200);
}
