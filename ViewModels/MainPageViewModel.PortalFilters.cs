using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace IptvPlayer.ViewModels;


public partial class MainPageViewModel
{

    private List<PortalGenreFilter> _portalGenreFilters = new();


    private List<PortalYearFilter> _portalYearFilters = new();


    private List<PortalCategoryInfo> _portalCategories = new();


    // Snapshot of the full portal catalog (all categories) captured before the first server-filtered load
    private List<ChannelViewModel>? _portalFullCatalog;


    private bool _isPortalSource;


    private bool _isVodSource;


    public bool IsVodSource => _isVodSource;


    // Poster grid toggle applies to portals and m3u VOD catalogs alike
    public Visibility IsPosterViewAvailable => IsContentTypeFilterVisible == Visibility.Visible || _isVodSource
        ? Visibility.Visible
        : Visibility.Collapsed;


    public void SetVodSource(bool isVod)
    {
        _isVodSource = isVod;
        OnPropertyChanged(nameof(IsVodSource));
        OnPropertyChanged(nameof(IsPosterViewAvailable));
        OnPropertyChanged(nameof(IsGroupFilterVisible));
    }


    private bool _suppressFilterLoad;


    public PlaylistSource? PortalSource { get; set; }

    private CancellationTokenSource? _filterLoadCts;


    private bool _isLoadingFiltered;


    public void SetPortalInfo(PlaylistSource source, List<PortalGenreFilter> genres, List<PortalYearFilter> years, List<PortalCategoryInfo> categories)
    {
        PortalSource = source;
        _portalGenreFilters = genres;
        _portalYearFilters = years;
        _portalCategories = categories;
        _portalFullCatalog = null;
        _portalCategoryIdsByFid.Clear();
        _isPortalSource = true;

        _suppressFilterLoad = true;
        try
        {
            ContentTypes.Clear();
            ContentTypes.Add(AllContentTypesOption);
            foreach (var cat in categories)
            {
                ContentTypes.Add(cat.Title);
            }
            SelectedContentType = AllContentTypesOption;

            Genres.Clear();
            Genres.Add(AllGenresOption);
            foreach (var g in genres)
            {
                if (!string.IsNullOrWhiteSpace(g.Title))
                {
                    Genres.Add(g.Title);
                }
            }
            SelectedGenre = AllGenresOption;

            Years.Clear();
            Years.Add(AllYearsOption);
            foreach (var y in years)
            {
                if (!string.IsNullOrWhiteSpace(y.Title))
                {
                    Years.Add(y.Title);
                }
            }
            SelectedYear = AllYearsOption;
        }
        finally
        {
            _suppressFilterLoad = false;
        }

        OnPropertyChanged(nameof(SelectedContentType));
        OnPropertyChanged(nameof(SelectedGenre));
        OnPropertyChanged(nameof(SelectedYear));

        OnPropertyChanged(nameof(IsContentTypeFilterVisible));
        OnPropertyChanged(nameof(IsGroupFilterVisible));
        OnPropertyChanged(nameof(IsGenreFilterVisible));
        OnPropertyChanged(nameof(IsYearFilterVisible));
    }


    public void ClearPortalInfo()
    {
        PortalSource = null;
        _portalGenreFilters.Clear();
        _portalYearFilters.Clear();
        _portalCategories.Clear();
        _portalFullCatalog = null;
        _portalCategoryIdsByFid.Clear();
        _isPortalSource = false;
        SetVodSource(false);

        _suppressFilterLoad = true;
        try
        {
            ContentTypes.Clear();
            SelectedContentType = AllContentTypesOption;

            Genres.Clear();
            Genres.Add(AllGenresOption);
            SelectedGenre = AllGenresOption;

            Years.Clear();
            Years.Add(AllYearsOption);
            SelectedYear = AllYearsOption;
        }
        finally
        {
            _suppressFilterLoad = false;
        }

        OnPropertyChanged(nameof(SelectedContentType));
        OnPropertyChanged(nameof(SelectedGenre));
        OnPropertyChanged(nameof(SelectedYear));

        OnPropertyChanged(nameof(IsContentTypeFilterVisible));
        OnPropertyChanged(nameof(IsGroupFilterVisible));
        OnPropertyChanged(nameof(IsGenreFilterVisible));
        OnPropertyChanged(nameof(IsYearFilterVisible));
    }


    public void ResetPortalFilters()
    {
        if (!_isPortalSource || PortalSource == null) return;

        _suppressFilterLoad = true;
        try
        {
            SelectedContentType = AllContentTypesOption;
            SelectedGenre = AllGenresOption;
            SelectedYear = AllYearsOption;
        }
        finally
        {
            _suppressFilterLoad = false;
        }

        OnPropertyChanged(nameof(SelectedContentType));
        OnPropertyChanged(nameof(SelectedGenre));
        OnPropertyChanged(nameof(SelectedYear));

        _ = LoadFilteredFromServerAsync();
    }


    private async Task LoadFilteredFromServerAsync()
    {
        if (PortalSource == null) return;

        var fid = ResolveCurrentFid();

        int? genreId = null;
        var genreTitle = string.Empty;
        if (!string.IsNullOrEmpty(SelectedGenre) && SelectedGenre != AllGenresOption)
        {
            var match = _portalGenreFilters.FirstOrDefault(
                g => string.Equals(g.Title, SelectedGenre, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                genreId = match.Id;
                genreTitle = match.Title;
            }
        }

        string? yearRange = null;
        if (!string.IsNullOrEmpty(SelectedYear) && SelectedYear != AllYearsOption)
        {
            yearRange = SelectedYear;
        }

        var hasFilters = genreId != null || yearRange != null;

        if (fid <= 0 && !hasFilters)
        {
            // "All types" without filters — restore the full catalog snapshot
            if (_portalFullCatalog is { Count: > 0 } full)
            {
                Channels = new ObservableCollection<ChannelViewModel>(full);
                UpdateChannelCountText();
                FilterChannels();
            }
            return;
        }

        // Capture the full catalog once, before the first server-filtered load replaces Channels
        if (_portalFullCatalog == null && Channels.Count > 0)
        {
            _portalFullCatalog = Channels.ToList();
        }

        // No Dispose: token may still be registered in an in-flight request
        _filterLoadCts?.Cancel();
        _filterLoadCts = new CancellationTokenSource();
        var ct = _filterLoadCts.Token;

        IsFilterLoading = true;
        _isLoadingFiltered = true;
        try
        {
            // Id set built locally from the snapshot avoids the slow network fallback
            HashSet<long>? categoryIds = null;
            if (hasFilters && fid > 0)
            {
                categoryIds = GetSnapshotCategoryIds(fid);
            }

            var items = await _videoPortalService.LoadFilteredAsync(
                PortalSource, fid, genreId, yearRange, ct, categoryIds);

            if (ct.IsCancellationRequested) return;

            var channels = items.Select(item => new ChannelViewModel
            {
                Name = item.Name,
                Group = item.Group,
                LogoUrl = item.LogoUrl,
                StreamUrl = item.StreamUrl,
                PortalRequest = item.RequestJson,
                Description = item.Description,
                Year = item.Year,
                Genre = item.Genre ?? genreTitle
            }).ToList();

            Channels = new ObservableCollection<ChannelViewModel>(channels);
            UpdateChannelCountText();

            FilterChannels();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка серверной загрузки фильтра: жанр={Genre}, год={Year}.", SelectedGenre, SelectedYear);
        }
        finally
        {
            _isLoadingFiltered = false;
            IsFilterLoading = false;
        }
    }


    private int ResolveCurrentFid()
    {
        if (!string.IsNullOrEmpty(SelectedContentType) && SelectedContentType != AllContentTypesOption)
        {
            var match = _portalCategories.FirstOrDefault(
                c => string.Equals(c.Title, SelectedContentType, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match.Fid;
            }
        }

        // "All types" (or unmatched title) — no category constraint
        return 0;
    }


    private readonly Dictionary<int, HashSet<long>> _portalCategoryIdsByFid = new();


    // Builds the category id set from the full-catalog snapshot (zero network requests);
    // empty result is not cached so a stale snapshot can retry via the service fallback
    private HashSet<long> GetSnapshotCategoryIds(int fid)
    {
        if (_portalCategoryIdsByFid.TryGetValue(fid, out var cached))
        {
            return cached;
        }

        var ids = new HashSet<long>();
        var title = _portalCategories.FirstOrDefault(c => c.Fid == fid)?.Title;
        if (_portalFullCatalog is { Count: > 0 } snapshot && !string.IsNullOrEmpty(title))
        {
            foreach (var channel in snapshot)
            {
                if (!string.Equals(channel.Group, title, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (VideoPortalService.TryExtractItemId(channel.PortalRequest) is { } id)
                {
                    ids.Add(id);
                }
            }
        }

        if (ids.Count > 0)
        {
            _portalCategoryIdsByFid[fid] = ids;
        }

        return ids;
    }
}
