using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace IptvPlayer.ViewModels;


public partial class MainPageViewModel
{
    private bool _isOnlineCinemaSource;
    private bool _isCinemaRefreshRunning;
    private bool _isLoadingMoreCinema;
    private bool _siteSearchRunning;
    private int? _lastSyncedYear;

    // Site quick-search results per query: hit page URLs stay visible in the
    // filtered list even when the query doesn't match the (Russian) title text
    private readonly Dictionary<string, HashSet<string>> _siteSearchHits = new(StringComparer.OrdinalIgnoreCase);

    private readonly ICatalogDatabaseService _onlineCinemaDb;
    private readonly OnlineCinemaCatalogService _onlineCinemaCatalog;
    private readonly IOnlineCinemaStreamResolver _onlineCinemaResolver;


    public bool IsOnlineCinemaSource => _isOnlineCinemaSource;


    public Visibility IsLoadMoreVisible => _isOnlineCinemaSource ? Visibility.Visible : Visibility.Collapsed;


    public Visibility IsLoadMoreBusyVisible => _isLoadingMoreCinema ? Visibility.Visible : Visibility.Collapsed;


    public void SetOnlineCinemaSource(bool isOnlineCinema)
    {
        _isOnlineCinemaSource = isOnlineCinema;
        OnPropertyChanged(nameof(IsOnlineCinemaSource));
        OnPropertyChanged(nameof(IsOnlineCinemaSortVisible));
        OnPropertyChanged(nameof(IsLoadMoreVisible));
        OnPropertyChanged(nameof(IsLoadMoreBusyVisible));
        OnPropertyChanged(nameof(IsGroupFilterVisible));
    }


    // Coarse group for the category combo: series by their page path,
    // everything else is a film. Site genres stay in Genre (multi-genre aware)
    internal static string GroupForItem(OnlineCinemaItem item) =>
        item.PageUrl.Contains("/serialy/", StringComparison.OrdinalIgnoreCase)
            ? L.T("OnlineCinema_Group_Serials")
            : L.T("OnlineCinema_Group_Films");

    internal static ChannelViewModel ItemToChannel(OnlineCinemaItem item) => new()
    {
        Name = item.Title,
        LogoUrl = item.PosterUrl,
        Group = GroupForItem(item),
        PageUrl = item.PageUrl,
        Description = item.Description,
        Year = item.Year,
        Genre = string.Join(",", item.Genres)
    };


    public async Task<List<ChannelViewModel>> LoadOnlineCinemaFromDbAsync()
    {
        var items = await _onlineCinemaDb.GetItemsAsync(KinogoSite.Id);
        return items.Select(ItemToChannel).ToList();
    }


    public async Task ReloadOnlineCinemaChannelsAsync()
    {
        // Playlist switched away — stale background work must not rebuild the list
        if (!_isOnlineCinemaSource)
        {
            return;
        }

        var channels = await LoadOnlineCinemaFromDbAsync();
        var selected = SelectedChannel;
        var group = SelectedGroup;
        // The rebuild resets the combos via OnChannelsChanged → RefreshGroups();
        // the second RefreshGroups below restores the group only, so genre/year
        // are snapshotted and put back explicitly (a first-ever year sync used
        // to clear the year selection)
        var genre = SelectedGenre;
        var year = SelectedYear;

        Channels = new ObservableCollection<ChannelViewModel>(channels);

        // Rebuild resets the combos — keep the user's group/genre/year and
        // re-point the selection at the fresh instance of the playing item
        RefreshGroups(group, keepFilters: true);
        _suppressFilterLoad = true;
        if (!string.IsNullOrEmpty(genre) && Genres.Contains(genre))
        {
            SelectedGenre = genre;
        }
        if (!string.IsNullOrEmpty(year) && Years.Contains(year))
        {
            SelectedYear = year;
        }
        _suppressFilterLoad = false;
        RestoreOnlineCinemaSortSelection();
        if (selected != null)
        {
            var fresh = Channels.FirstOrDefault(
                c => !string.IsNullOrEmpty(c.PageUrl) && c.PageUrl == selected.PageUrl);
            if (fresh != null)
            {
                fresh.IsPlaying = selected.IsPlaying;
                SelectedChannel = fresh;
            }
        }
    }


    // First sync: one page per category with progress in the loading overlay
    public async Task<bool> InitialSyncOnlineCinemaAsync()
    {
        try
        {
            await _onlineCinemaCatalog.RefreshFirstPagesAsync();
            await ReloadOnlineCinemaChannelsAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Онлайн-кинотеатр: первичная синхронизация каталога не удалась.");
            return false;
        }
    }


    // Background collection (playback timer): deepens the catalog by one page
    // per tick; playlist refreshes are skipped while it runs
    public bool IsOnlineCinemaSyncActive => _onlineCinemaCatalog.IsSyncActive;

    // Visible list is re-read from the catalog DB at most once per the
    // configured interval while background collection runs
    private DateTime _lastCinemaListRefresh = DateTime.UtcNow;

    // Background collect/refresh run under this source; a playlist switch
    // cancels it so a finishing task cannot rebuild the other playlist's list
    private CancellationTokenSource _cinemaBackgroundCts = new();

    private CancellationToken BeginCinemaBackgroundWork()
    {
        if (_cinemaBackgroundCts.IsCancellationRequested)
        {
            _cinemaBackgroundCts.Dispose();
            _cinemaBackgroundCts = new CancellationTokenSource();
        }

        return _cinemaBackgroundCts.Token;
    }

    // Playlist switch: abort in-flight cinema work
    public void CancelOnlineCinemaBackgroundWork() => _cinemaBackgroundCts.Cancel();

    public async Task OnlineCinemaBackgroundCollectAsync()
    {
        if (_isCinemaRefreshRunning)
        {
            return;
        }

        var ct = BeginCinemaBackgroundWork();
        try
        {
            var loaded = await _onlineCinemaCatalog.SyncNextBackgroundPageAsync(ct);
            if (loaded && !ct.IsCancellationRequested)
            {
                var refreshMinutes = Math.Clamp(AppSettings.OnlineCinemaListRefreshMinutes, 0, 24 * 60);
                if (refreshMinutes == 0 ||
                    DateTime.UtcNow - _lastCinemaListRefresh < TimeSpan.FromMinutes(refreshMinutes))
                {
                    _logger.LogDebug("Онлайн-кинотеатр: страница каталога добавлена в БД, список не обновлялся.");
                    return;
                }

                _lastCinemaListRefresh = DateTime.UtcNow;
                await ReloadOnlineCinemaChannelsAsync();
                _logger.LogInformation("Онлайн-кинотеатр: фоновый сбор добавил страницу каталога, список обновлён.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Playlist switched mid-page — the DB keeps what was loaded
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Онлайн-кинотеатр: фоновый сбор страницы не удался.");
        }
    }


    // Background refresh on source open: page 1 of each stale category is
    // re-fetched so new films join the catalog without blocking the UI
    public async Task RefreshOnlineCinemaInBackgroundAsync()
    {
        if (_isCinemaRefreshRunning || _onlineCinemaCatalog.IsSyncActive)
        {
            return;
        }

        _isCinemaRefreshRunning = true;
        var ct = BeginCinemaBackgroundWork();
        try
        {
            await _onlineCinemaCatalog.RefreshFirstPagesAsync(ct);
            await ReloadOnlineCinemaChannelsAsync();
            _logger.LogInformation("Онлайн-кинотеатр: фоновое обновление первых страниц завершено.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Playlist switched mid-refresh — the DB keeps what was fetched
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Онлайн-кинотеатр: фоновое обновление не удалось.");
        }
        finally
        {
            _isCinemaRefreshRunning = false;
        }
    }


    // Loads the next page of the selected category (or any category with more
    // pages) and merges it into the channel list
    [RelayCommand]
    public async Task LoadMoreOnlineCinemaAsync()
    {
        if (_isLoadingMoreCinema)
        {
            return;
        }

        _isLoadingMoreCinema = true;
        OnPropertyChanged(nameof(IsLoadMoreBusyVisible));
        try
        {
            // "Load more" follows the selection: series deepen their own
            // listing, films deepen the selected genre (falling back to
            // "all films" when the genre is not a site category); any other
            // group keeps the round-robin over site categories
            List<OnlineCinemaItem>? items = null;
            string? category = null;
            if (SelectedGroup == L.T("OnlineCinema_Group_Serials"))
            {
                category = "сериалы";
            }
            else if (SelectedGroup == L.T("OnlineCinema_Group_Films"))
            {
                var genre = SelectedGenre;
                category = !string.IsNullOrEmpty(genre) && genre != AllGenresOption &&
                           KinogoSite.Categories.ContainsKey(genre)
                    ? genre
                    : "все фильмы";
            }

            if (category != null)
            {
                items = await _onlineCinemaCatalog.LoadNextPageAsync(category);
            }
            else
            {
                foreach (var cat in KinogoSite.Categories.Keys)
                {
                    items = await _onlineCinemaCatalog.LoadNextPageAsync(cat);
                    if (items is { Count: > 0 })
                    {
                        break;
                    }
                }
            }

            if (items is { Count: > 0 })
            {
                await ReloadOnlineCinemaChannelsAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Онлайн-кинотеатр: не удалось догрузить страницу каталога.");
        }
        finally
        {
            _isLoadingMoreCinema = false;
            OnPropertyChanged(nameof(IsLoadMoreBusyVisible));
        }
    }


    // Site-side year sync: the first page differs per year, so when the user
    // picks a year the site is re-queried with its session year filter and the
    // fresh page lands in the catalog under a "год N" pseudo-category
    // Sort combo → DLE xsort POST for the category matching the current
    // group/genre; the sorted page 1 replaces the cached one and the list
    // reloads. Empty value resets to the site default
    private bool _suppressOnlineCinemaSort;

    public async Task ApplyOnlineCinemaSortAsync(string sortValue)
    {
        if (_suppressOnlineCinemaSort || !_isOnlineCinemaSource)
        {
            return;
        }

        var category = CurrentCinemaCategory();
        if (category == null)
        {
            return;
        }

        if (string.IsNullOrEmpty(sortValue))
        {
            AppSettings.OnlineCinemaSorts.Remove(category);
        }
        else
        {
            AppSettings.OnlineCinemaSorts[category] = sortValue;
        }

        _ = _settingsService.SaveAsync(AppSettings);
        UpdateOnlineCinemaFilters(reloadCurrentPage: false);
        await ReloadCurrentCinemaCategoryAsync(category);
    }

    // Restores the combo from the stored sort for the current category
    // Push the persisted per-category sorts into the catalog service
    public void SyncCatalogSorts() => _onlineCinemaCatalog.Sorts = AppSettings.OnlineCinemaSorts;

    // The category matching the current group/genre selection (films →
    // genre-or-"все фильмы"; series → "сериалы"); null for other groups
    private string? CurrentCinemaCategory()
    {
        if (SelectedGroup == L.T("OnlineCinema_Group_Serials"))
        {
            return "сериалы";
        }

        if (SelectedGroup == L.T("OnlineCinema_Group_Films"))
        {
            var genre = SelectedGenre;
            return !string.IsNullOrEmpty(genre) && genre != AllGenresOption &&
                   KinogoSite.Categories.ContainsKey(genre)
                ? genre
                : "все фильмы";
        }

        return null;
    }

    // Pushes the current year + per-category sorts into the catalog service
    public void UpdateOnlineCinemaFilters(bool reloadCurrentPage)
    {
        if (!_isOnlineCinemaSource)
        {
            return;
        }

        SyncCatalogSorts();
        var year = int.TryParse(SelectedYear, out var y) ? y.ToString() : "";
        foreach (var cat in KinogoSite.Categories.Keys.Concat(new[] { "сериалы" }))
        {
            _onlineCinemaCatalog.SetCategoryFilter(cat, year,
                AppSettings.OnlineCinemaSorts.GetValueOrDefault(cat) ?? "");
        }

        if (reloadCurrentPage)
        {
            var category = CurrentCinemaCategory();
            if (category != null)
            {
                _ = ReloadCurrentCinemaCategoryAsync(category);
            }
        }
    }

    private int _categoryReloadBusy;

    private async Task ReloadCurrentCinemaCategoryAsync(string category)
    {
        // Re-fetches page 1 with the session filters applied and refreshes the
        // list. Re-entrancy guard: the reload re-fires combo handlers, which
        // must not start another fetch cycle
        if (Interlocked.Exchange(ref _categoryReloadBusy, 1) == 1)
        {
            return;
        }

        try
        {
            try
            {
                await _onlineCinemaCatalog.LoadCategoryPageAsync(category, 1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Онлайн-кинотеатр: не удалось обновить страницу «{Category}».", category);
            }

            await ReloadOnlineCinemaChannelsAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _categoryReloadBusy, 0);
        }
    }

    // On open the sort combo starts at the site default ("По умолчанию");
    // per-category stored sorts only come back when the user picks a group
    public void ResetOnlineCinemaSortToDefault()
    {
        _suppressOnlineCinemaSort = true;
        SelectedOnlineCinemaSort = OnlineCinemaSortOptions[0];
        _suppressOnlineCinemaSort = false;
    }

    public void RestoreOnlineCinemaSortSelection()
    {
        string category;
        if (SelectedGroup == L.T("OnlineCinema_Group_Serials"))
        {
            category = "сериалы";
        }
        else if (SelectedGroup == L.T("OnlineCinema_Group_Films"))
        {
            var genre = SelectedGenre;
            category = !string.IsNullOrEmpty(genre) && genre != AllGenresOption &&
                       KinogoSite.Categories.ContainsKey(genre)
                ? genre
                : "все фильмы";
        }
        else
        {
            return;
        }

        AppSettings.OnlineCinemaSorts.TryGetValue(category, out var stored);
        _suppressOnlineCinemaSort = true;
        SelectedOnlineCinemaSort = OnlineCinemaSortOptions.FirstOrDefault(o => o.Value == stored)
            ?? OnlineCinemaSortOptions[0];
        _suppressOnlineCinemaSort = false;
    }

    public void ApplyOnlineCinemaYear()
    {
        if (_lastSyncedYear?.ToString() == SelectedYear)
        {
            return;
        }

        _lastSyncedYear = int.TryParse(SelectedYear, out var parsed) ? parsed : (int?)null;
        UpdateOnlineCinemaFilters(reloadCurrentPage: true);
    }

    // Online cinema site search (lightsearch): called when local filtering
    // finds nothing. Hits are saved to the catalog (pseudo-category "поиск")
    // and remembered per query so the filtered list keeps showing them
    public async Task<bool> SearchOnlineCinemaSiteAsync(string query, CancellationToken ct = default)
    {
        query = query.Trim();
        if (!_isOnlineCinemaSource || query.Length < 2 || _siteSearchRunning ||
            _siteSearchHits.ContainsKey(query))
        {
            return false;
        }

        _siteSearchRunning = true;
        try
        {
            var items = await _onlineCinemaCatalog.SearchAsync(query, ct);
            if (items == null)
            {
                return false; // transport failed — leave the query retryable
            }

            _siteSearchHits[query] = items.Select(i => i.PageUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (items.Count > 0)
            {
                await ReloadOnlineCinemaChannelsAsync();
            }

            _logger.LogInformation("Онлайн-кинотеатр: поиск по сайту «{Query}» — {Count} результатов.",
                query, items.Count);
            return items.Count > 0;
        }
        catch (OperationCanceledException)
        {
            // Query changed mid-search — the fresh query re-triggers on debounce
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Онлайн-кинотеатр: поиск по сайту «{Query}» не удался.", query);
            return false;
        }
        finally
        {
            _siteSearchRunning = false;
        }
    }
}
