using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.ViewModels;

/// <summary>
/// Portal filter logic: server-side genre/year/type filtering for video portal.
/// </summary>
public partial class MainPageViewModel
{
    /// <summary>Жанры из manifest.controls.filters (id → title) для серверных фильтров.</summary>
    private List<PortalGenreFilter> _portalGenreFilters = new();

    /// <summary>Года из manifest.controls.filters (title → years-value) для серверных фильтров.</summary>
    private List<PortalYearFilter> _portalYearFilters = new();

    /// <summary>Категории видео-портала из manifest (fid → title) для фильтра типа контента.</summary>
    private List<PortalCategoryInfo> _portalCategories = new();

    /// <summary>Загружен ли каталог из портала (серверные фильтры доступны).</summary>
    private bool _isPortalSource;

    /// <summary>
    /// Флаг подавления серверной перезагрузки при программном сбросе
    /// фильтров (SetPortalInfo / ClearPortalInfo / ResetPortalFilters).
    /// </summary>
    private bool _suppressFilterLoad;

    /// <summary>
    /// Текущий источник портала для серверных фильтров (свой на каждую сессию).
    /// </summary>
    public PlaylistSource? PortalSource { get; set; }

    private CancellationTokenSource? _filterLoadCts;

    /// <summary>Идёт ли серверная загрузка фильтра (не сбрасывать жанр/год в RefreshGroups).</summary>
    private bool _isLoadingFiltered;

    /// <summary>
    /// Устанавливает информацию об источнике портала для серверных фильтров.
    /// </summary>
    public void SetPortalInfo(PlaylistSource source, List<PortalGenreFilter> genres, List<PortalYearFilter> years, List<PortalCategoryInfo> categories)
    {
        PortalSource = source;
        _portalGenreFilters = genres;
        _portalYearFilters = years;
        _portalCategories = categories;
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

    /// <summary>Сбрасывает информацию об источнике портала (M3U-плейлист).</summary>
    public void ClearPortalInfo()
    {
        PortalSource = null;
        _portalGenreFilters.Clear();
        _portalYearFilters.Clear();
        _portalCategories.Clear();
        _isPortalSource = false;

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

    /// <summary>
    /// Сброс всех фильтров портала к дефолтным значениям.
    /// </summary>
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

    /// <summary>
    /// Серверная загрузка с фильтрами типа контента/жанра/года.
    /// </summary>
    private async Task LoadFilteredFromServerAsync()
    {
        if (PortalSource == null) return;

        var fid = ResolveCurrentFid();
        if (fid <= 0) return;

        _filterLoadCts?.Cancel();
        _filterLoadCts = new CancellationTokenSource();
        var ct = _filterLoadCts.Token;

        IsFilterLoading = true;
        _isLoadingFiltered = true;
        try
        {
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

            var items = await _videoPortalService.LoadFilteredAsync(
                PortalSource, fid, genreId, yearRange, ct);

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

    /// <summary>
    /// Определяет fid текущей категории по выбранному типу контента.
    /// </summary>
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

        return _portalCategories.Count > 0 ? _portalCategories[0].Fid : 0;
    }

    /// <summary>Извлекает fid из JSON-запроса элемента портала (0, если не удалось).</summary>
    private static int ExtractFidFromRequest(string? requestJson)
    {
        if (string.IsNullOrEmpty(requestJson)) return 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(requestJson);
            if (doc.RootElement.TryGetProperty("fid", out var fidProp) &&
                fidProp.ValueKind == System.Text.Json.JsonValueKind.Number &&
                fidProp.TryGetInt32(out var fid))
            {
                return fid;
            }
        }
        catch (System.Text.Json.JsonException) { }
        return 0;
    }
}
