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
    private int _lastSyncedYear;

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


    internal static ChannelViewModel ItemToChannel(OnlineCinemaItem item) => new()
    {
        Name = item.Title,
        LogoUrl = item.PosterUrl,
        Group = item.Category,
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
        Channels = new ObservableCollection<ChannelViewModel>(channels);
        SelectedChannel = selected;
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
                await ReloadOnlineCinemaChannelsAsync();
                _logger.LogInformation("Онлайн-кинотеатр: фоновый сбор добавил страницу каталога.");
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
            List<OnlineCinemaItem>? items = null;
            var category = SelectedGroup != AllGroupsOption && SelectedGroup != FavoritesOption
                ? SelectedGroup
                : null;
            if (category != null && KinogoSite.Categories.ContainsKey(category))
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
}
