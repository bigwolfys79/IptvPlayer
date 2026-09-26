using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    private int _lastSyncedYear;

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
        var channels = await LoadOnlineCinemaFromDbAsync();
        var selected = SelectedChannel;
        var group = SelectedGroup;

        Channels = new ObservableCollection<ChannelViewModel>(channels);

        // Rebuild resets the combos — keep the user's group/genre/year and
        // re-point the selection at the fresh instance of the playing item
        RefreshGroups(group, keepFilters: true);
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

    public async Task OnlineCinemaBackgroundCollectAsync()
    {
        if (_isCinemaRefreshRunning)
        {
            return;
        }

        try
        {
            var loaded = await _onlineCinemaCatalog.SyncNextBackgroundPageAsync();
            if (loaded)
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
        try
        {
            await _onlineCinemaCatalog.RefreshFirstPagesAsync();
            await ReloadOnlineCinemaChannelsAsync();
            _logger.LogInformation("Онлайн-кинотеатр: фоновое обновление первых страниц завершено.");
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
    public async Task SyncOnlineCinemaYearAsync(int year)
    {
        if (_lastSyncedYear == year)
        {
            return;
        }

        _lastSyncedYear = year;
        try
        {
            await _onlineCinemaCatalog.SyncYearAsync(year);
            await ReloadOnlineCinemaChannelsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Онлайн-кинотеатр: не удалось синхронизировать год {Year}.", year);
        }
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
