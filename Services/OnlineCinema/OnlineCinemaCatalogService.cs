using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;

// Orchestrates catalog loading for the online cinema: first sync loads page 1 of
// every category, later opens refresh stale first pages in the background (new
// films appear on page 1), deeper pages load on demand as the user scrolls.
// Pages are fetched over plain HTTP (the site WAF passes in-site-looking
// requests — verified live); the hidden WebView2 is only a fallback.
// All methods must be called on the UI thread (WebView2 requirement).
public class OnlineCinemaCatalogService
{
    private const int Page1RefreshHours = 6;
    private const int PauseBetweenPagesMs = 2000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

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

    private volatile bool _syncActive;

    // True while a background collection page is in flight — playlist refreshes
    // are skipped for its duration
    public bool IsSyncActive => _syncActive;

    private int _backgroundCursor;

    // Called from the playback timer (~every 2 minutes): deepens the catalog by
    // one page, round-robin over categories that already have a first page.
    // Returns true when a page was loaded
    public async Task<bool> SyncNextBackgroundPageAsync(CancellationToken ct = default)
    {
        if (_syncActive)
        {
            return false;
        }

        _syncActive = true;
        try
        {
            var categories = KinogoSite.Categories.Keys.ToList();
            var pages = await _db.GetLoadedPagesAsync(KinogoSite.Id);
            for (var n = 0; n < categories.Count; n++)
            {
                ct.ThrowIfCancellationRequested();
                var category = categories[(_backgroundCursor + n) % categories.Count];
                var categoryPages = pages.Where(p => p.Category == category).ToList();
                if (categoryPages.Count == 0)
                {
                    continue; // category not started — the first sync owns it
                }

                var next = categoryPages.Max(p => p.PageNumber) + 1;
                var total = categoryPages.Max(p => p.TotalPages);
                if (total > 0 && next > total)
                {
                    continue; // exhausted
                }

                try
                {
                    await LoadCategoryPageAsync(category, next, ct);
                    _backgroundCursor = (categories.IndexOf(category) + 1) % categories.Count;
                    return true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Онлайн-кинотеатр: фоновая догрузка {Category} стр. {Page} не удалась.",
                        category, next);
                }
            }

            return false;
        }
        finally
        {
            _syncActive = false;
        }
    }

    // Pure-HTTP page fetch: null means "not usable" (challenge/403/empty) — the
    // caller falls back to the hidden WebView2. curl.exe goes first (its TLS
    // passes the WAF where .NET gets challenged), plain HttpClient is the
    // second tier, the hidden WebView2 the last one
    private async Task<(List<OnlineCinemaItem> Items, int TotalPages)?> TryLoadPageHttpAsync(
        string url, string category, CancellationToken ct)
    {
        if (!KinogoSite.IsValidPageUrl(url))
        {
            return null;
        }

        foreach (var viaCurl in new[] { true, false })
        {
            string? html;
            if (viaCurl)
            {
                if (!await CurlHttp.GetAvailabilityAsync())
                {
                    continue;
                }

                var result = await CurlHttp.GetAsync(url, KinogoSite.BaseUrl + "/", iframe: false, ct);
                html = result is { Status: 200 } ? result.Body : null;
            }
            else
            {
                try
                {
                    using var msg = new HttpRequestMessage(HttpMethod.Get, url);
                    KinogoSite.ApplyBrowserHeaders(msg);
                    using var resp = await Http.SendAsync(msg, ct);
                    html = resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
                }
                catch (Exception)
                {
                    html = null;
                }
            }

            if (html == null || KinogoSite.LooksLikeChallenge(html))
            {
                continue;
            }

            var (items, total) = KinogoSite.ParseCategoryPageHtml(html, category);
            if (items.Count > 0)
            {
                return (items, total);
            }
        }

        return null;
    }

    private async Task<List<OnlineCinemaItem>> SavePageAsync(
        string category, int page, List<OnlineCinemaItem> items, int total)
    {
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

    public async Task<List<OnlineCinemaItem>> LoadCategoryPageAsync(string category, int page, CancellationToken ct = default)
    {
        var url = KinogoSite.CategoryUrl(category, page);
        var httpResult = await TryLoadPageHttpAsync(url, category, ct);
        if (httpResult != null)
        {
            return await SavePageAsync(category, page, httpResult.Value.Items, httpResult.Value.TotalPages);
        }

        if (!_browser.IsInitialized)
        {
            await _browser.InitializeAsync();
        }

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
        return await SavePageAsync(category, page, items, total);
    }

    // Called when the source opens: page 1 of every category is (re)loaded when
    // missing or stale, everything else comes from the DB — instant open.
    // Skipped while a background collection page is in flight
    public async Task RefreshFirstPagesAsync(CancellationToken ct = default)
    {
        if (_syncActive)
        {
            return;
        }

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

    // Site-side year listing: /xfsearch/god/<year>/ is fetched over plain HTTP
    // and stored under the "год N" pseudo-category. The site's first page
    // differs per year, so the year is re-requested rather than only filtered
    // locally. Browser fallback for when the WAF blocks plain HTTP
    public async Task<List<OnlineCinemaItem>?> SyncYearAsync(int year, CancellationToken ct = default)
    {
        var yearCategory = YearCategoryKey(year);
        var httpResult = await TryLoadPageHttpAsync(KinogoSite.YearUrl(year), yearCategory, ct);
        if (httpResult != null)
        {
            return await SavePageAsync(yearCategory, 1, httpResult.Value.Items, httpResult.Value.TotalPages);
        }

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

        return await LoadCategoryPageAsync(yearCategory, 1, ct);
    }

    private int JitteredPause() => PauseBetweenPagesMs + _random.Next(0, 1200);
}
