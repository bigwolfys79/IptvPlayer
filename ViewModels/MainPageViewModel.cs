using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace IptvPlayer.ViewModels;

/// <summary>
/// Корневая ViewModel страницы (этап 2 MVVM): списки каналов, выбранный канал,
/// вложенные ViewModel и команды — избранное, напоминания, расписание записей,
/// запись канала, пауза архива, возврат к эфиру, показ/скрытие EPG, фильтрация.
/// </summary>
public partial class MainPageViewModel : ObservableObject
{
    private const string AllGroupsOption = "Все группы";
    private const string AllGenresOption = "Все жанры";
    private const string AllYearsOption = "Все годы";
    private const string FavoritesOption = "★ Избранное";

    private readonly ISettingsService _settingsService;
    private readonly IVideoPortalService _videoPortalService;
    private readonly ILogger<MainPageViewModel> _logger;

    // Все свойства ниже — ручные (поле + SetProperty) вместо [ObservableProperty]:
    // сгенерированные генератором в WinUI-сценариях не создают WinRT-проекторов
    // (предупреждение MVVMTK0045), а семантика INotifyPropertyChanged та же.
    private EpgViewModel _epgViewModel;

    /// <summary>Жанры из manifest.controls.filters (id → title) для серверных фильтров.</summary>
    private List<Services.PortalGenreFilter> _portalGenreFilters = new();

    /// <summary>Года из manifest.controls.filters (title → years-value) для серверных фильтров.</summary>
    private List<Services.PortalYearFilter> _portalYearFilters = new();

    /// <summary>Категории видео-портала из manifest (fid → title) для фильтра типа контента.</summary>
    private List<Services.PortalCategoryInfo> _portalCategories = new();

    /// <summary>Загружен ли каталог из портала (серверные фильтры доступны).</summary>
    private bool _isPortalSource;

    /// <summary>
    /// Флаг подавления серверной перезагрузки при программном сбросе
    /// фильтров (SetPortalInfo / ClearPortalInfo / ResetPortalFilters).
    /// Без него установка SelectedXxx = AllXxxOption в этих методах
    /// запускала LoadFilteredFromServerAsync — дублирующую загрузку
    /// каталога, который уже загружен в LoadCatalogAsync.
    /// </summary>
    private bool _suppressFilterLoad;

    /// <summary>
    /// Текущий источник портала для серверных фильтров (свой на каждую сессию).
    /// Заполняется при загрузке каталога портала из code-behind.
    /// </summary>
    public Models.PlaylistSource? PortalSource { get; set; }

    public EpgViewModel EpgViewModel
    {
        get => _epgViewModel;
        set => SetProperty(ref _epgViewModel, value);
    }

    /// <summary>Управление воспроизведением (плееры, архив, громкость).</summary>
    public PlayerViewModel Player { get; }

    /// <summary>Запись каналов/передач через ffmpeg.exe.</summary>
    public RecordingService Recording { get; }

    private ObservableCollection<ChannelViewModel> _channels = new();

    public ObservableCollection<ChannelViewModel> Channels
    {
        get => _channels;
        set
        {
            if (SetProperty(ref _channels, value))
            {
                OnChannelsChanged(value);
            }
        }
    }

    private ObservableCollection<ChannelViewModel> _displayedChannels = new();

    public ObservableCollection<ChannelViewModel> DisplayedChannels
    {
        get => _displayedChannels;
        set => SetProperty(ref _displayedChannels, value);
    }

    private ChannelViewModel? _selectedChannel;

    public ChannelViewModel? SelectedChannel
    {
        get => _selectedChannel;
        set => SetProperty(ref _selectedChannel, value);
    }

    private bool _isEpgVisible;

    public bool IsEpgVisible
    {
        get => _isEpgVisible;
        set => SetProperty(ref _isEpgVisible, value);
    }

    private bool _isRecording;

    public bool IsRecording
    {
        get => _isRecording;
        set => SetProperty(ref _isRecording, value);
    }

    private string _searchQuery = string.Empty;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                OnSearchQueryChanged(value);
            }
        }
    }

    private string _selectedGroup = AllGroupsOption;

    public string SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                OnSelectedGroupChanged(value);
            }
        }
    }

    private ObservableCollection<string> _groups = new();

    public ObservableCollection<string> Groups
    {
        get => _groups;
        set => SetProperty(ref _groups, value);
    }

    private string _selectedGenre = AllGenresOption;

    public string SelectedGenre
    {
        get => _selectedGenre;
        set
        {
            if (SetProperty(ref _selectedGenre, value))
            {
                OnSelectedGenreChanged(value);
            }
        }
    }

    private ObservableCollection<string> _genres = new();

    public ObservableCollection<string> Genres
    {
        get => _genres;
        set => SetProperty(ref _genres, value);
    }

    public Visibility IsGenreFilterVisible => Genres.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    private string _selectedYear = "Все годы";

    public string SelectedYear
    {
        get => _selectedYear;
        set
        {
            if (SetProperty(ref _selectedYear, value))
            {
                OnSelectedYearChanged(value);
            }
        }
    }

    private ObservableCollection<string> _years = new();

    public ObservableCollection<string> Years
    {
        get => _years;
        set => SetProperty(ref _years, value);
    }

    public Visibility IsYearFilterVisible => Years.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    // ===================== Тип контента видео-портала =====================

    private const string AllContentTypesOption = "Все типы";

    private string _selectedContentType = AllContentTypesOption;

    /// <summary>Выбранный тип контента (fid категории) в комбобоксе портала.</summary>
    public string SelectedContentType
    {
        get => _selectedContentType;
        set
        {
            if (SetProperty(ref _selectedContentType, value))
            {
                OnSelectedContentTypeChanged(value);
            }
        }
    }

    private ObservableCollection<string> _contentTypes = new();

    /// <summary>Типы контента из manifest (Фильмы, Сериалы и т.д.).</summary>
    public ObservableCollection<string> ContentTypes
    {
        get => _contentTypes;
        set => SetProperty(ref _contentTypes, value);
    }

    /// <summary>Показывать ли комбобокс типа контента (только для портальных источников).</summary>
    public Visibility IsContentTypeFilterVisible => _isPortalSource && ContentTypes.Count > 1
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>Показывать ли комбобокс групп (только для M3U-источников).</summary>
    public Visibility IsGroupFilterVisible => !_isPortalSource
        ? Visibility.Visible
        : Visibility.Collapsed;

    private bool _isFilterLoading;

    /// <summary>Идёт ли серверная загрузка фильтра (показать прогресс-индикатор).</summary>
    public bool IsFilterLoading
    {
        get => _isFilterLoading;
        set => SetProperty(ref _isFilterLoading, value);
    }

    private CancellationTokenSource? _filterLoadCts;

    /// <summary>Идёт ли серверная загрузка фильтра (не сбрасывать жанр/год в RefreshGroups).</summary>
    private bool _isLoadingFiltered;

    private string _channelCountText = "Каналов: 0";

    public string ChannelCountText
    {
        get => _channelCountText;
        set => SetProperty(ref _channelCountText, value);
    }

    private string? _recordError;

    public string? RecordError
    {
        get => _recordError;
        set => SetProperty(ref _recordError, value);
    }

    /// <summary>
    /// Каноническая копия настроек на сессию. Наполняется в InitializeAsync
    /// кодом представления; ViewModel управляет избранном, напоминаниями,
    /// последним каналом и при необходимости сохраняет.
    /// </summary>
    public AppSettings AppSettings { get; set; } = new();

    // ===================== Таймер сна =====================

    private DateTime? _sleepTimerEndTime;

    /// <summary>Время срабатывания таймера сна (UTC) или null, если не активен.</summary>
    public DateTime? SleepTimerEndTime
    {
        get => _sleepTimerEndTime;
        private set
        {
            if (SetProperty(ref _sleepTimerEndTime, value))
            {
                OnPropertyChanged(nameof(IsSleepTimerActive));
                SleepTimerChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Активен ли таймер сна.</summary>
    public bool IsSleepTimerActive => _sleepTimerEndTime != null;

    /// <summary>Оставшееся время в формате mm:ss или null.</summary>
    public string? SleepTimerRemainingText
    {
        get
        {
            if (_sleepTimerEndTime == null) return null;
            var remaining = _sleepTimerEndTime.Value - DateTime.UtcNow;
            if (remaining.TotalSeconds <= 0) return "00:00";
            return $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
        }
    }

    /// <summary>Запускает таймер сна на указанное количество минут.</summary>
    public void StartSleepTimer(int minutes)
    {
        if (minutes <= 0)
        {
            StopSleepTimer();
            return;
        }

        SleepTimerEndTime = DateTime.UtcNow.AddMinutes(minutes);
        _logger.LogInformation("Таймер сна запущен на {Minutes} мин, сработает в {EndTime:HH:mm:ss}.", minutes, SleepTimerEndTime.Value.ToLocalTime());
    }

    /// <summary>Останавливает таймер сна.</summary>
    public void StopSleepTimer()
    {
        if (_sleepTimerEndTime == null) return;
        SleepTimerEndTime = null;
        _logger.LogInformation("Таймер сна остановлен.");
    }

    /// <summary>
    /// Проверка каждую секунду — не пора ли остановить воспроизведение.
    /// Вызывается из секундного таймера в code-behind.
    /// </summary>
    public void CheckSleepTimer()
    {
        if (_sleepTimerEndTime == null) return;

        OnPropertyChanged(nameof(SleepTimerRemainingText));

        if (DateTime.UtcNow >= _sleepTimerEndTime.Value)
        {
            _logger.LogInformation("Таймер сна сработал — остановка воспроизведения.");
            SleepTimerEndTime = null;
            SleepTimerExpired?.Invoke(this, EventArgs.Empty);
        }
    }

    // ===================== События =====================

    /// <summary>Сменилось состояние записи — обновить кнопки в панелях.</summary>
    public event EventHandler? RecordingChanged;

    /// <summary>Переключена видимость EPG-оверлея — представление двигает панель.</summary>
    public event EventHandler? EpgVisibilityChanged;

    /// <summary>Пользователь изменил настройку — код-behind должен запустить дебаунс-таймер.</summary>
    public event EventHandler? SettingsSaveRequested;

    /// <summary>Нужно показать тост-напоминание — код-behind показывает UI-тост.</summary>
    public event EventHandler<ProgramReminder>? ReminderToastRequested;

    /// <summary>Список каналов/фильтр изменился — код-behind перестраивает группы оверлея.</summary>
    public event EventHandler? FilterChanged;

    /// <summary>Нужно проскроллить EPG к текущей программе — код-behind вызывает ScrollIntoView.</summary>
    public event EventHandler? ScrollToProgramRequested;

    /// <summary>Ошибка при попытке воспроизвести архив — код-behind показывает StreamError.</summary>
    public event EventHandler<string>? ArchivePlayErrorRequested;

    /// <summary>Таймер сна истёк — код-behind останавливает воспроизведение.</summary>
    public event EventHandler? SleepTimerExpired;

    /// <summary>Изменилось состояние таймера сна — код-behind обновляет UI.</summary>
    public event EventHandler? SleepTimerChanged;

    public MainPageViewModel(
        EpgViewModel epgViewModel,
        ISettingsService settingsService,
        IVideoPortalService videoPortalService,
        PlayerViewModel player,
        RecordingService recording,
        ILogger<MainPageViewModel> logger)
    {
        _epgViewModel = epgViewModel;
        _settingsService = settingsService;
        _videoPortalService = videoPortalService;
        _logger = logger;
        Player = player;
        Recording = recording;
        _selectedChannel = new ChannelViewModel(); // избегаем null для x:Bind путей

        // Старт/фinish любой записи (в т.ч. самозавершение по -t) обновляет
        // состояние кнопок: IsRecording = «ТЕКУЩИЙ канал пишется», а не «хоть
        // что-то пишется» — записей теперь может быть несколько.
        Recording.RecordingsChanged += (s, e) =>
        {
            IsRecording = Recording.IsRecordingStream(SelectedChannel?.StreamUrl);
            RecordingChanged?.Invoke(this, EventArgs.Empty);
        };

        // После (пере)загрузки EPG пересобираем DisplayedChannels — обновлённые
        // иконки и текущие передачи гарантированно перерисовываются, даже если
        // PropertyChanged пришёл из фонового потока и привязка его не получила.
        // Выбранный канал восстанавливаем: Clear() внутри FilterChannels
        // сбрасывает SelectedItem ListView в null.
        _epgViewModel.EpgReloaded += (_, _) =>
        {
            var selected = SelectedChannel;
            FilterChannels();
            SelectedChannel = selected;
        };
    }

    // ===================== Автоматические реакции на смену свойств =====================

    // Поиск по каталогу портала — это тысячи элементов: пересборка списка
    // (FilterChannels + сгруппированный оверлей) на каждый символ намертво
    // вешала UI. Фильтруем один раз, когда пользователь перестал печатать.
    private System.Threading.CancellationTokenSource? _searchDebounceCts;

    private void OnSearchQueryChanged(string value)
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts = new System.Threading.CancellationTokenSource();
        var token = _searchDebounceCts.Token;
        _ = DebouncedFilterAsync(token);
    }

    private async Task DebouncedFilterAsync(System.Threading.CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        FilterChannels();
    }

    private void OnSelectedGroupChanged(string value) => FilterChannels();

    private void OnSelectedContentTypeChanged(string value)
    {
        if (_suppressFilterLoad) return;
        if (_isPortalSource && PortalSource != null)
        {
            _ = LoadFilteredFromServerAsync();
        }
        else
        {
            FilterChannels();
        }
    }

    private void OnSelectedGenreChanged(string value)
    {
        if (_suppressFilterLoad) return;
        if (_isPortalSource && PortalSource != null)
        {
            _ = LoadFilteredFromServerAsync();
        }
        else
        {
            FilterChannels();
        }
    }

    private void OnSelectedYearChanged(string value)
    {
        if (_suppressFilterLoad) return;
        if (_isPortalSource && PortalSource != null)
        {
            _ = LoadFilteredFromServerAsync();
        }
        else
        {
            FilterChannels();
        }
    }

    /// <summary>
    /// Устанавливает информацию об источнике портала для серверных фильтров.
    /// Вызывается из code-behind после загрузки каталога.
    /// </summary>
    public void SetPortalInfo(Models.PlaylistSource source, List<Services.PortalGenreFilter> genres, List<Services.PortalYearFilter> years, List<Services.PortalCategoryInfo> categories)
    {
        PortalSource = source;
        _portalGenreFilters = genres;
        _portalYearFilters = years;
        _portalCategories = categories;
        _isPortalSource = true;

        // Подавляем серверную перезагрузку при установке дефолтов —
        // каталог уже загружен в LoadCatalogAsync, а каждый OnSelectedXxxChanged
        // без этого флага запускал бы LoadFilteredFromServerAsync (дубль).
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

            // Жанры для портала берём напрямую из manifest.controls.filters
            // (а не из Channels — у каталога жанр не проставлен до применения
            // фильтра). Иначе Genres содержал бы только «Все жанры» и комбобокс
            // оставался скрытым, хотя в manifest приходит 28 жанров.
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

            // Года тоже берём из manifest (80+ пунктов: конкретные года и
            // диапазоны вроде «2021-2026»). Раньше Years наполнялся из
            // Channels[i].Year, но год в каталоге может отсутствовать или
            // парситься как строка — комбобокс оставался скрытым.
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

        // Принудительно пушим PropertyChanged для SelectedXxx, даже если
        // SetProperty внутри сеттера вернул false (значение не изменилось).
        // Без этого ComboBox после Clear+Add не пере-select'ит дефолтный
        // элемент и остаётся пустым визуально, хотя SelectedItem
        // в ViewModel корректен.
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

        // Подавляем серверную перезагрузку при сбросе (как в SetPortalInfo).
        _suppressFilterLoad = true;
        try
        {
            ContentTypes.Clear();
            SelectedContentType = AllContentTypesOption;

            // Возврат в M3U-режим: Genres снова наполняется из Channels
            // в RefreshGroups — оставляем только заглушку «Все жанры».
            Genres.Clear();
            Genres.Add(AllGenresOption);
            SelectedGenre = AllGenresOption;

            // Возврат в M3U-режим: Years тоже наполняется из Channels
            // в RefreshGroups — оставляем только заглушку «Все годы».
            Years.Clear();
            Years.Add(AllYearsOption);
            SelectedYear = AllYearsOption;
        }
        finally
        {
            _suppressFilterLoad = false;
        }

        // Принудительный PropertyChanged — ComboBox после Clear без
        // этого остаётся пустым даже при корректном SelectedItem в VM.
        OnPropertyChanged(nameof(SelectedContentType));
        OnPropertyChanged(nameof(SelectedGenre));
        OnPropertyChanged(nameof(SelectedYear));

        OnPropertyChanged(nameof(IsContentTypeFilterVisible));
        OnPropertyChanged(nameof(IsGroupFilterVisible));
        OnPropertyChanged(nameof(IsGenreFilterVisible));
        OnPropertyChanged(nameof(IsYearFilterVisible));
    }

    /// <summary>
    /// Сброс всех фильтров портала к дефолтным значениям:
    /// «Все типы», «Все жанры», «Все годы». Запускает одну
    /// серверную перезагрузку с итоговым состоянием (а не три
    /// отдельных, как было бы без _suppressFilterLoad).
    /// </summary>
    public void ResetPortalFilters()
    {
        if (!_isPortalSource || PortalSource == null) return;

        // Подавляем серверную перезагрузку на каждое присваивание —
        // иначе получили бы три LoadFilteredFromServerAsync подряд.
        // Достаточно одной в конце с финальным состоянием фильтров.
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

        // Принудительный PropertyChanged — гарантия, что ComboBox
        // отрисует дефолтные пункты, даже если SelectedXxx формально
        // не изменился (например, был уже «Все жанры»).
        OnPropertyChanged(nameof(SelectedContentType));
        OnPropertyChanged(nameof(SelectedGenre));
        OnPropertyChanged(nameof(SelectedYear));

        // Одна серверная перезагрузка с итоговым состоянием.
        _ = LoadFilteredFromServerAsync();
    }

    /// <summary>
    /// Серверная загрузка с фильтрами типа контента/жанра/года. Вызывается при смене
    /// типа контента, жанра или года в ComboBox для портальных источников.
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

            // ВАЖНО: Channels обновился, но UI показывается из
            // DisplayedChannels. Без FilterChannels() список визуально
            // не менялся — пользователь менял фильтр, сервер возвращал
            // новый набор, но на экране оставался старый.
            // Клиентские фильтры в FilterChannels проходят весь набор
            // насквозь (сервер уже отфильтровал по fid/genre/year),
            // так что это безопасно.
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
    /// Если тип не выбран («Все типы») — берёт fid из первой категории.
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

    private int _sortModeIndex;

    /// <summary>
    /// Сортировка списка: 0 — как в каталоге (порядок портала/плейлиста),
    /// 1 — по имени, 2 — по году убыванием (портал; у каналов M3U год 0
    /// и они уходят в конец). Избранное остаётся наверху при любой сортировке.
    /// </summary>
    public int SortModeIndex
    {
        get => _sortModeIndex;
        set
        {
            if (SetProperty(ref _sortModeIndex, value))
            {
                FilterChannels();
            }
        }
    }

    private void OnChannelsChanged(ObservableCollection<ChannelViewModel> value)
    {
        RefreshGroups();
        FilterChannels();
        UpdateChannelCountText();
        _portalSeasonGroups = null;
    }

    // ===================== Сезоны портала =====================
    // Сезоны сериала — отдельные элементы каталога («Название. Сезон N» /
    // «Сезон N-M»); группировка по базовому названию даёт комбобокс сезона.
    private Dictionary<string, List<ChannelViewModel>>? _portalSeasonGroups;

    /// <summary>
    /// Соседние сезоны сериала портала (включая сам канал), отсортированные
    /// по номерам сезонов. Один элемент — фильм/сериал без пометки сезона.
    /// </summary>
    public List<ChannelViewModel> GetPortalSeasonSiblings(ChannelViewModel channel)
    {
        if (string.IsNullOrEmpty(channel.PortalRequest))
        {
            return new List<ChannelViewModel> { channel };
        }

        _portalSeasonGroups ??= BuildPortalSeasonGroups();
        if (ParsePortalSeasonName(channel.Name).BaseName is not { } baseName ||
            !_portalSeasonGroups.TryGetValue(baseName, out var group))
        {
            return new List<ChannelViewModel> { channel };
        }

        return group;
    }

    private Dictionary<string, List<ChannelViewModel>> BuildPortalSeasonGroups()
    {
        var groups = new Dictionary<string, List<ChannelViewModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in Channels)
        {
            if (string.IsNullOrEmpty(channel.PortalRequest))
            {
                continue;
            }

            if (ParsePortalSeasonName(channel.Name).BaseName is not { } baseName)
            {
                continue;
            }

            if (!groups.TryGetValue(baseName, out var list))
            {
                groups[baseName] = list = new List<ChannelViewModel>();
            }

            list.Add(channel);
        }

        foreach (var key in groups.Keys.ToList())
        {
            groups[key].Sort((a, b) => SeasonSortKey(a.Name).CompareTo(SeasonSortKey(b.Name)));
        }

        return groups;
    }

    /// <summary>
    /// «Название. Сезон 3. (2021)» → («Название», (3, 3));
    /// «Название. Сезон 1-7» → («Название», (1, 7)).
    /// BaseName null — пометки сезона нет (фильм/сериал одной карточкой).
    /// </summary>
    internal static (string? BaseName, (int From, int To)? Season) ParsePortalSeasonName(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            name,
            @"^(.*?)[\s.]*Сезон\s+(\d+)(?:\s*[-–]\s*(\d+))?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return (null, null);
        }

        var baseName = match.Groups[1].Value.Trim().TrimEnd('.').Trim();
        if (baseName.Length == 0)
        {
            return (null, null);
        }

        var from = int.Parse(match.Groups[2].Value);
        var to = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : from;
        return (baseName, (from, to));
    }

    private static (int From, int To) SeasonSortKey(string name) =>
        ParsePortalSeasonName(name).Season ?? (int.MaxValue, int.MaxValue);

    /// <summary>
    /// Пересобирает список и группы после изменения настроек родительского
    /// контроля (или истечения временной разблокировки): если выбранная
    /// группа оказалась скрыта — сбрасываем на «Все группы».
    /// </summary>
    // ===================== Родительский контроль =====================

    /// <summary>
    /// UI показывает диалог PIN (с выбором длительности отключения запроса)
    /// и возвращает: null — отменено; 0 — «до выключения»; n>0 — минут.
    /// </summary>
    public event Func<ChannelViewModel, Task<int?>>? ParentalUnlockRequested;

    /// <summary>Внешняя точка для путей запуска вне команды (автопродолжение).</summary>
    public Task<bool> CanPlayChannelAsync(ChannelViewModel channel)
        => EnsureChannelAllowedAsync(channel);

    /// <summary>
    /// Разрешён ли запуск канала: группы из списка блокировки при включённом
    /// контроле требуют PIN. При верном PIN сразу offered длительность
    /// отключения запроса и канал запускается.
    /// </summary>
    private async Task<bool> EnsureChannelAllowedAsync(ChannelViewModel channel)
    {
        if (!ParentalControlService.IsLocked(AppSettings) ||
            !ParentalControlService.IsGroupBlocked(AppSettings, channel.Group))
        {
            return true;
        }

        var handler = ParentalUnlockRequested;
        if (handler == null)
        {
            return false; // некому спросить PIN — не запускаем.
        }

        var result = await handler(channel);
        if (result == null)
        {
            _logger.LogInformation("Ввод PIN отменён — канал {Channel} не запущен.", channel.Name);
            return false; // отменено/неверный PIN.
        }

        _logger.LogInformation(
            "PIN принят: запрос отключается на {Minutes} мин, запуск канала {Channel}.",
            result == 0 ? -1 : result, channel.Name);
        ParentalControlService.Unlock(AppSettings, result == 0 ? null : result);
        SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ===================== Фильтрация каналов =====================

    /// <summary>
    /// Пересчитывает DisplayedChannels с учётом текста поиска, выбранного типа
    /// контента (портал) или группы (M3U), жанра и года.
    /// Избранные каналы всегда стоят первыми.
    /// </summary>
    public void FilterChannels()
    {
        var query = SearchQuery?.Trim() ?? string.Empty;

        IEnumerable<ChannelViewModel> filtered = Channels;

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (_isPortalSource)
        {
            // Портал: фильтрацию по типу контента/жанру/году выполняет САМ
            // сервер в LoadFilteredFromServerAsync → LoadFilteredAsync.
            // Раньше здесь дополнительно фильтровали по fid из PortalRequest
            // каждого элемента, но это flick-идентификатор (12345), а не fid
            // категории (1/2/3...). Сравнение 12345 == 2 всегда ложно, и
            // список превращался в пустой. Аналогично с жанром/годом:
            // сервер уже отфильтровал, дублирующий клиентский фильтр
            // лишь отсекал элементы с незаполненным полем (например, у
            // части элементов нет year — они бы выпали).
            // Сейчас доверяем серверу: клиент фильтрует только по строке
            // поиска (это единственный фильтр, не имеющий серверного аналога).
        }
        else
        {
            // M3U: фильтр по группе + жанру + году целиком на клиенте.
            var selectedGroup = SelectedGroup;
            if (!string.IsNullOrEmpty(selectedGroup) && selectedGroup == FavoritesOption)
            {
                filtered = filtered.Where(c => c.IsFavorite);
            }
            else if (!string.IsNullOrEmpty(selectedGroup) && selectedGroup != AllGroupsOption)
            {
                filtered = filtered.Where(c => string.Equals(c.Group?.Trim(), selectedGroup, StringComparison.OrdinalIgnoreCase));
            }

            var selectedGenre = SelectedGenre;
            if (!string.IsNullOrEmpty(selectedGenre) && selectedGenre != AllGenresOption)
            {
                filtered = filtered.Where(c => string.Equals(c.Genre?.Trim(), selectedGenre, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(SelectedYear) && SelectedYear != AllYearsOption)
            {
                if (int.TryParse(SelectedYear, out var year))
                {
                    filtered = filtered.Where(c => c.Year == year);
                }
                else if (SelectedYear.Contains('-'))
                {
                    var parts = SelectedYear.Split('-', '–');
                    if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out var from) && int.TryParse(parts[1].Trim(), out var to))
                    {
                        filtered = filtered.Where(c => c.Year >= from && c.Year <= to);
                    }
                }
            }
        }

        // Избранные — наверху списка при любом фильтре и сортировке.
        filtered = SortModeIndex switch
        {
            1 => filtered.OrderByDescending(c => c.IsFavorite)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
            2 => filtered.OrderByDescending(c => c.IsFavorite)
                .ThenByDescending(c => c.Year)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderByDescending(c => c.IsFavorite)
        };

        var selected = SelectedChannel;

        // Замена коллекции целиком: одна смена ItemsSource вместо тысяч
        // событий CollectionChanged — на каталоге в 20k+ элементов это
        // главное, что держало UI при пересборке. Выделение в контролах
        // восстанавливает MainPage по событию FilterChanged (OneWay-привязка
        // выделения не перепушит сама — SelectedChannel не меняется).
        DisplayedChannels = new ObservableCollection<ChannelViewModel>(filtered);

        if (selected != null && SelectedChannel == null && DisplayedChannels.Contains(selected))
        {
            SelectedChannel = selected;
        }

        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Пересобирает список групп для GroupFilterComboBox на основе текущих Channels.
    /// </summary>
    public void RefreshGroups(string? previouslySelected = null)
    {
        var groups = Channels
            .Select(c => c.Group)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Groups.Clear();
        Groups.Add(AllGroupsOption);
        Groups.Add(FavoritesOption);
        foreach (var group in groups)
        {
            Groups.Add(group);
        }

        SelectedGroup = (previouslySelected != null && Groups.Contains(previouslySelected))
            ? previouslySelected
            : AllGroupsOption;

        // Для портальных источников Genres уже заполнен из manifest в
        // SetPortalInfo — у каталога жанр не проставлен, и rebuild из
        // Channels обнулил бы список и скрыл комбобокс. Только M3U.
        if (!_isPortalSource)
        {
            var genres = Channels
                .Select(c => c.Genre)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Genres.Clear();
            Genres.Add(AllGenresOption);
            foreach (var genre in genres)
            {
                Genres.Add(genre);
            }

            // При серверной загрузке фильтра не сбрасываем выбранные жанр/год.
            if (!_isLoadingFiltered)
            {
                SelectedGenre = AllGenresOption;
            }
        }
        else if (!_isLoadingFiltered)
        {
            SelectedGenre = AllGenresOption;
        }

        OnPropertyChanged(nameof(IsGenreFilterVisible));

        // Для портальных источников Years уже заполнен из manifest в
        // SetPortalInfo — rebuild из Channels обнулил бы список и скрыл
        // комбобокс. Только M3U.
        if (!_isPortalSource)
        {
            var years = Channels
                .Where(c => c.Year > 0)
                .Select(c => c.Year)
                .Distinct()
                .OrderByDescending(y => y)
                .Select(y => y.ToString())
                .ToList();

            Years.Clear();
            Years.Add(AllYearsOption);
            foreach (var year in years)
            {
                Years.Add(year);
            }

            if (!_isLoadingFiltered)
            {
                SelectedYear = AllYearsOption;
            }
        }
        else if (!_isLoadingFiltered)
        {
            SelectedYear = AllYearsOption;
        }
        OnPropertyChanged(nameof(IsYearFilterVisible));
    }

    public void UpdateChannelCountText()
    {
        ChannelCountText = L.T(
            $"Каналов: {Channels.Count}",
            $"Channels: {Channels.Count}");
    }

    // ===================== Избранные каналы =====================

    /// <summary>
    /// Переключает избранное канала. Каналы идентифицируются по имени
    /// (Id нестабилен между запусками).
    /// </summary>
    [RelayCommand]
    private void ToggleFavorite(ChannelViewModel channel)
    {
        channel.IsFavorite = !channel.IsFavorite;

        if (channel.IsFavorite)
        {
            if (!AppSettings.FavoriteChannels.Contains(channel.Name, StringComparer.OrdinalIgnoreCase))
            {
                AppSettings.FavoriteChannels.Add(channel.Name);
            }
        }
        else
        {
            AppSettings.FavoriteChannels.RemoveAll(
                n => string.Equals(n, channel.Name, StringComparison.OrdinalIgnoreCase));
        }

        SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
        FilterChannels();
    }

    // ===================== Выбор и воспроизведение канала =====================

    /// <summary>История просмотра для кнопки/клавиши «предыдущий канал».</summary>
    public ChannelHistory ChannelHistory { get; } = new();

    // Переход по истории не должен записывать покидаемый канал в историю
    // снова — иначе «назад» ходил бы по кругу между двумя каналами.
    private bool _navigatingBack;

    [RelayCommand]
    private async Task GoToPreviousChannelAsync()
    {
        var previous = ChannelHistory.Pop();
        if (previous == null)
        {
            return;
        }

        _navigatingBack = true;
        try
        {
            await SelectAndPlayChannelAsync(previous);
        }
        finally
        {
            _navigatingBack = false;
        }
    }

    /// <summary>
    /// Обработчик клика по каналу: останавливает текущее воспроизведение
    /// (если это другой канал или архив), запускает прямой эфир, загружает EPG.
    /// </summary>
    [RelayCommand]
    private async Task SelectAndPlayChannelAsync(ChannelViewModel channel)
    {
        if (!await EnsureChannelAllowedAsync(channel))
        {
            return;
        }

        // Запоминаем покидаемый канал как «предыдущий» для кнопки «назад».
        if (!_navigatingBack &&
            Player.CurrentPlayerChannelId is int previousId &&
            previousId != channel.Id &&
            Channels.FirstOrDefault(c => c.Id == previousId) is { } previous)
        {
            ChannelHistory.Record(previous);
        }

        // Повторный клик по каналу, когда играет его архив, должен вернуть
        // прямой эфир, а не застрять в ветке "тот же канал — пауза/резюм".
        if (Player.CurrentPlayerChannelId != null &&
            (Player.CurrentPlayerChannelId != channel.Id || Player.IsArchivePlaying))
        {
            Player.Stop();
        }

        SelectedChannel = channel;

        // Запоминаем последний смотренный канал для автопродолжения
        // при следующем запуске (дебаунс в code-behind не даёт писать файл на каждый клик).
        // Пишем в активный плейлист — у каждого плейлиста своё автопродолжение.
        var activePlaylist = AppSettings.Playlists.FirstOrDefault(p => p.Id == AppSettings.ActivePlaylistId);
        if (activePlaylist != null)
        {
            activePlaylist.LastWatchedChannel = channel.Name;
        }
        AppSettings.LastWatchedChannel = channel.Name;
        SettingsSaveRequested?.Invoke(this, EventArgs.Empty);

        // Канал заигрывает сразу по выбору — без отдельного нажатия "Воспроизвести".
        await PlayChannelAsync(channel);

        await EpgViewModel.LoadEPGForChannelAsync(channel.Id);
        ApplyReminderFlags();
        ScrollToProgramRequested?.Invoke(this, EventArgs.Empty);
    }

    // ===================== Архивная передача =====================

    /// <summary>
    /// Клик по передаче в списке EPG: уже начавшаяся передача запускается в архиве
    /// (timeshift) с её начала. Будущие передачи недоступны.
    /// </summary>
    [RelayCommand]
    private async Task PlayArchiveEntryAsync(EPGEntry entry)
    {
        var channel = SelectedChannel;
        if (channel == null || string.IsNullOrWhiteSpace(channel.StreamUrl))
        {
            ArchivePlayErrorRequested?.Invoke(this, L.T(
                "У канала нет URL потока — архив недоступен.",
                "Channel has no stream URL — archive unavailable."));
            return;
        }

        if (!await EnsureChannelAllowedAsync(channel))
        {
            return;
        }

        if (entry.StartTime > DateTime.Now)
        {
            ArchivePlayErrorRequested?.Invoke(this, L.T(
                "Эта передача ещё не началась.",
                "This programme has not started yet."));
            return;
        }

        var archiveUrl = ArchiveUrlBuilder.BuildUrl(channel.StreamUrl, entry.StartTime);
        await Player.StartPlaybackAsync(channel, archiveUrl, entry);

        // Запустили архив — EPG-оверлей больше не нужен и только перекрывает видео.
        if (IsEpgVisible)
        {
            IsEpgVisible = false;
            EpgVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ===================== Напоминания о передачах =====================

    /// <summary>
    /// Колокольчик в карточке передачи EPG: ставит/снимает напоминание.
    /// Доступно только будущим передачам.
    /// </summary>
    [RelayCommand]
    private void ToggleReminder(EPGEntry entry)
    {
        var channel = SelectedChannel;
        if (channel == null || entry.StartTime <= DateTime.Now)
        {
            return;
        }

        var existing = AppSettings.ProgramReminders.FirstOrDefault(
            r => r.ChannelId == channel.Id && r.StartTime == entry.StartTime);

        if (existing != null)
        {
            AppSettings.ProgramReminders.Remove(existing);
            entry.HasReminder = false;
        }
        else
        {
            AppSettings.ProgramReminders.Add(new ProgramReminder
            {
                ChannelId = channel.Id,
                ChannelName = channel.Name,
                ProgramName = entry.ProgramName,
                StartTime = entry.StartTime
            });
            entry.HasReminder = true;
        }

        SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Восстанавливает флаги колокольчиков на передачах после (пере)загрузки
    /// EPG — коллекции EPGEntries пересобираются, рантайм-флаг HasReminder
    /// при этом сбрасывается.
    /// </summary>
    public void ApplyReminderFlags()
    {
        var now = DateTime.Now;

        var activeReminders = AppSettings.ProgramReminders
            .Where(r => !r.Notified && r.StartTime > now)
            .Select(r => (r.ChannelId, r.StartTime))
            .ToHashSet();

        // Запланированные записи — та же идентификация (канал+время начала).
        var activeRecords = AppSettings.ScheduledRecordings
            .Where(r => r.StartTime > now)
            .Select(r => (ChannelName: r.ChannelName, r.StartTime))
            .ToHashSet();

        foreach (var channel in Channels)
        {
            foreach (var entry in channel.EPGEntries)
            {
                entry.HasReminder = activeReminders.Contains((channel.Id, entry.StartTime));
                entry.HasScheduleRecord = activeRecords.Contains((channel.Name, entry.StartTime));
            }
        }
    }

    /// <summary>
    /// Периодическая проверка (раз в 30 с): показать тосты по напоминаниям,
    /// до начала которых осталось меньше ReminderMinutes, и убрать устаревшие.
    /// </summary>
    public async Task CheckRemindersAsync()
    {
        try
        {
            var now = DateTime.Now;
            var window = TimeSpan.FromMinutes(Math.Max(1, AppSettings.ReminderMinutes));
            var changed = false;

            for (var i = AppSettings.ProgramReminders.Count - 1; i >= 0; i--)
            {
                var reminder = AppSettings.ProgramReminders[i];

                // Передача давно началась (или прошла) — напоминание больше
                // не актуально ни для показа, ни для хранения.
                if (reminder.StartTime <= now - window)
                {
                    AppSettings.ProgramReminders.RemoveAt(i);
                    changed = true;
                    continue;
                }

                if (!reminder.Notified && reminder.StartTime - now <= window)
                {
                    ReminderToastRequested?.Invoke(this, reminder);
                    reminder.Notified = true;
                    changed = true;
                }
            }

            if (changed)
            {
                await SaveSettingsAsync();
                ApplyReminderFlags();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckRemindersAsync: не удалось проверить напоминания.");
        }
    }

    // ===================== Запланированные записи =====================

    /// <summary>
    /// Кнопка записи в карточке будущей передачи EPG: ставит/снимает
    /// запланированную запись.
    /// </summary>
    [RelayCommand]
    private void ToggleScheduleRecord(EPGEntry entry)
    {
        var channel = SelectedChannel;
        if (channel == null || entry.StartTime <= DateTime.Now)
        {
            return;
        }

        var existing = AppSettings.ScheduledRecordings.FirstOrDefault(
            r => r.ChannelName == channel.Name && r.StartTime == entry.StartTime);

        if (existing != null)
        {
            AppSettings.ScheduledRecordings.Remove(existing);
            entry.HasScheduleRecord = false;
        }
        else
        {
            AppSettings.ScheduledRecordings.Add(new ScheduledRecording
            {
                ChannelName = channel.Name,
                ProgramName = entry.ProgramName,
                StartTime = entry.StartTime,
                DurationSec = Math.Max(60, (entry.EndTime - entry.StartTime).TotalSeconds)
            });
            entry.HasScheduleRecord = true;
        }

        SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Проверка расписания записей: наступило время — разрешаем канал по имени
    /// и стартуем ffmpeg на оставшуюся длительность.
    /// </summary>
    public void CheckScheduledRecordings()
    {
        if (AppSettings.ScheduledRecordings.Count == 0)
        {
            return;
        }

        var now = DateTime.Now;
        var changed = false;

        for (var i = AppSettings.ScheduledRecordings.Count - 1; i >= 0; i--)
        {
            var rec = AppSettings.ScheduledRecordings[i];
            var end = rec.StartTime.AddSeconds(rec.DurationSec);

            if (now >= end)
            {
                // Передача давно прошла — расписание неактуально.
                AppSettings.ScheduledRecordings.RemoveAt(i);
                changed = true;
                continue;
            }

            if (now < rec.StartTime)
            {
                continue;
            }

            if (Recording.IsRecordingChannel(rec.ChannelName))
            {
                // Этот канал уже пишется (например, вручную) — не дублируем.
                AppSettings.ScheduledRecordings.RemoveAt(i);
                changed = true;
                continue;
            }

            var channel = Channels.FirstOrDefault(
                c => string.Equals(c.Name, rec.ChannelName, StringComparison.OrdinalIgnoreCase));
            if (channel == null || string.IsNullOrWhiteSpace(channel.StreamUrl))
            {
                AppSettings.ScheduledRecordings.RemoveAt(i);
                changed = true;
                continue;
            }

            var remaining = (int)Math.Max(60, (end - now).TotalSeconds);
            var started = Recording.Start(
                channel.StreamUrl,
                $"{rec.ChannelName} - {rec.ProgramName}",
                rec.ChannelName,
                remaining,
                AppSettings.RecordingsFolder);

            if (started != null)
            {
                AppSettings.ScheduledRecordings.RemoveAt(i);
                changed = true;
            }
            // null = лимит параллельных записей/нет ffmpeg — попробуем на
            // следующем тике таймера.
        }

        if (changed)
        {
            SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
            ApplyReminderFlags();
        }
    }

    /// <summary>Убирает передачу из расписания записей (кнопка в списке записей).</summary>
    [RelayCommand]
    private void RemoveScheduledRecording(ScheduledRecording rec)
    {
        AppSettings.ScheduledRecordings.Remove(rec);
        SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
        ApplyReminderFlags();
    }

    // ===================== Запись текущего канала =====================

    /// <summary>
    /// Запись текущего канала: старт (ffmpeg -c copy) либо стоп.
    /// При неудачном старте устанавливает RecordError для отображения в UI.
    /// </summary>
    [RelayCommand]
    private void ToggleRecording()
    {
        RecordError = null;

        var channel = SelectedChannel;
        if (channel == null || string.IsNullOrWhiteSpace(channel.StreamUrl))
        {
            return;
        }

        var existing = Recording.Active.FirstOrDefault(r => r.StreamUrl == channel.StreamUrl);
        if (existing != null)
        {
            // Пишется именно этот канал — кнопка его и останавливает
            // (остальные параллельные записи не трогаем).
            Recording.Stop(existing.Id);
        }
        else
        {
            var started = Recording.Start(
                channel.StreamUrl, channel.Name, channel.Name,
                durationSec: null, AppSettings.RecordingsFolder);
            if (started == null)
            {
                RecordError = L.T(
                    "Не удалось начать запись (ffmpeg недоступен или достигнут лимит одновременных записей) — см. лог.",
                    "Could not start recording (ffmpeg missing or concurrent recording limit reached) — see log.");
            }
        }

        IsRecording = Recording.IsRecordingStream(channel.StreamUrl);
        RecordingChanged?.Invoke(this, EventArgs.Empty);
    }

    // ===================== Пауза архива =====================

    /// <summary>
    /// Пауза/возобновление архивной передачи. Делегирует PlayerViewModel.
    /// </summary>
    [RelayCommand]
    private void ToggleArchivePause()
    {
        Player.ToggleArchivePause(SelectedChannel);
    }

    // ===================== Возврат к эфиру =====================

    /// <summary>
    /// Возврат из архива к прямому эфиру выбранного канала. Для элемента
    /// портала «эфир» — перезапуск фильма с начала (запрос нового потока).
    /// </summary>
    [RelayCommand]
    private async Task BackToLiveAsync()
    {
        var channel = SelectedChannel;
        if (channel != null)
        {
            await PlayChannelAsync(channel);
        }
    }

    /// <summary>
    /// Запуск канала/фильма: обычный канал — прямой эфир как раньше; элемент
    /// портала — воспроизведение в режиме VOD (пауза без рестарта потока).
    /// Фильмы (type "stream") уже несут url в каталоге; сериалы — url нет,
    /// поток запрашивается у портала по клику (лениво, не кэшируется).
    /// Возвращает true, если воспроизведение запущено.
    /// </summary>
    public async Task<bool> PlayChannelAsync(ChannelViewModel channel)
        => await PlayChannelAsync(channel, interactive: true);

    // ===================== Возобновление просмотра VOD =====================

    /// <summary>Позиций в настройках хватит надолго; старые вытесняются по UpdatedAt.</summary>
    private const int MaxVodResumeEntries = 200;

    /// <summary>Порог, с которого предложение «продолжить» имеет смысл.</summary>
    private const double MinVodResumeSeconds = 30;

    /// <summary>Досмотренное почти до конца не предлагаем продолжать.</summary>
    private const double VodResumeWatchedFraction = 0.95;

    private DateTime _lastVodResumeSaveRequest = DateTime.MinValue;

    /// <summary>
    /// Вопрос к пользователю «продолжить с сохранённого места?» — показывает
    /// диалог представление (там XamlRoot). true — продолжить с позиции.
    /// </summary>
    public event Func<string, TimeSpan, Task<bool>>? VodResumePromptRequested;

    /// <summary>Ключ сохранённой позиции: фильм — название, серия — название + индекс.</summary>
    internal static string VodResumeKey(string title, int episodeIndex)
        => episodeIndex >= 0 ? $"{title}::{episodeIndex}" : title;

    /// <summary>
    /// Сохранённая позиция этого VOD, если продолжать есть смысл: больше
    /// порога и не досмотрено до конца. Иначе null.
    /// </summary>
    public TimeSpan? GetSavedVodPosition(string title, int episodeIndex)
    {
        if (AppSettings.VodResumePositions.TryGetValue(VodResumeKey(title, episodeIndex),
                out var entry) && entry.PositionSeconds >= MinVodResumeSeconds &&
            (entry.DurationSeconds <= 0 ||
             entry.PositionSeconds <= entry.DurationSeconds * VodResumeWatchedFraction))
        {
            return TimeSpan.FromSeconds(entry.PositionSeconds);
        }

        return null;
    }

    /// <summary>
    /// Спрашивает пользователя, продолжать ли с сохранённого места.
    /// Возвращает позицию для resumePosition или null (смотреть сначала /
    /// диалога не было / сохранённой позиции нет).
    /// </summary>
    public async Task<TimeSpan?> OfferVodResumeAsync(string title, int episodeIndex)
    {
        var saved = GetSavedVodPosition(title, episodeIndex);
        if (saved == null || VodResumePromptRequested == null)
        {
            return null;
        }

        return await VodResumePromptRequested(title, saved.Value) ? saved : null;
    }

    /// <summary>
    /// Запоминает текущую позицию играющего VOD. Вызывается секундным таймером
    /// представления; запись в настройки — с прореживанием (запрос сохранения
    /// не чаще раза в 5 секунд), чтобы не писать файл каждую секунду.
    /// </summary>
    public void CaptureVodPosition()
    {
        if (!Player.IsVodPlaying || Player.VodChannel is not { } channel ||
            string.IsNullOrWhiteSpace(channel.Name))
        {
            return;
        }

        var position = Player.VodPositionSeconds;
        if (position < MinVodResumeSeconds)
        {
            // Начало просмотра: сохранённую позицию прошлого раза гасим,
            // чтобы в следующий раз не предлагали давно проигранное место.
            AppSettings.VodResumePositions.Remove(VodResumeKey(channel.Name, Player.CurrentVodEpisodeIndex));
            return;
        }

        AppSettings.VodResumePositions[VodResumeKey(channel.Name, Player.CurrentVodEpisodeIndex)] =
            new VodResumePosition
            {
                PositionSeconds = position,
                DurationSeconds = Player.VodDurationSeconds,
                EpisodeIndex = Player.CurrentVodEpisodeIndex,
                UpdatedAt = DateTime.Now
            };

        PruneVodResumeEntries();

        if ((DateTime.Now - _lastVodResumeSaveRequest).TotalSeconds >= 5)
        {
            _lastVodResumeSaveRequest = DateTime.Now;
            SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Досмотренное до конца и самые старые записи вытесняются.</summary>
    private void PruneVodResumeEntries()
    {
        var positions = AppSettings.VodResumePositions;
        var finished = positions.Where(kv => kv.Value.DurationSeconds > 0 &&
                                             kv.Value.PositionSeconds > kv.Value.DurationSeconds * VodResumeWatchedFraction)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in finished)
        {
            positions.Remove(key);
        }

        while (positions.Count > MaxVodResumeEntries)
        {
            var oldest = positions.OrderBy(kv => kv.Value.UpdatedAt).First().Key;
            positions.Remove(oldest);
        }
    }

    /// <summary>
    /// Запуск канала/фильма: обычный канал — прямой эфир как раньше; элемент
    /// портала — episodes-запрос (flick). Фильм играет сразу; у сериала при
    /// interactive=true представление спрашивает серию (событие
    /// PortalEpisodePickRequested), interactive=false (автопродолжение) —
    /// первая серия. Возвращает true, если воспроизведение запущено.
    /// </summary>
    public async Task<bool> PlayChannelAsync(ChannelViewModel channel, bool interactive)
    {
        if (!string.IsNullOrWhiteSpace(channel.PortalRequest))
        {
            var playlist = AppSettings.Playlists.FirstOrDefault(p => p.Id == AppSettings.ActivePlaylistId);
            if (playlist == null)
            {
                Player.StreamError = L.T("Источник портала не найден.", "Portal source not found.");
                return false;
            }

            // Фильм с готовой ссылкой каталога стартует СРАЗУ — без ожидания
            // flick-запроса (он и делал старт «долгим»). Варианты качества
            // догружаются фоном (LoadPortalVariantsInBackgroundAsync) и
            // подкладываются в играющий плеер.
            if (!string.IsNullOrWhiteSpace(channel.StreamUrl))
            {
                var catalogResume = interactive
                    ? await OfferVodResumeAsync(channel.Name, -1)
                    : null;
                await Player.StartPlaybackAsync(channel, channel.StreamUrl!, archiveEntry: null,
                    isVod: true, resumePosition: catalogResume);
                if (!string.IsNullOrWhiteSpace(channel.PortalRequest))
                {
                    _ = LoadPortalVariantsInBackgroundAsync(playlist, channel);
                }

                return true;
            }

            PortalFlickResult flick;
            Player.IsBuffering = true;
            try
            {
                flick = await _videoPortalService.ResolveEpisodesAsync(playlist, channel.PortalRequest);
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrWhiteSpace(channel.StreamUrl))
                {
                    // Ссылка из каталога ещё дышит — играем без вариантов качества.
                    _logger.LogWarning(ex,
                        "Портал: flick для «{Item}» не удался, используется ссылка из каталога.", channel.Name);
                    await Player.StartPlaybackAsync(channel, channel.StreamUrl, archiveEntry: null, isVod: true);
                    return true;
                }

                _logger.LogError(ex, "Портал: не удалось получить поток для «{Item}».", channel.Name);
                Player.StreamError = L.T(
                    $"Портал не отдал поток: {ex.Message}",
                    $"Portal did not return a stream: {ex.Message}");
                return false;
            }
            finally
            {
                Player.IsBuffering = false;
            }

            var episode = flick.Episodes[0];
            _logger.LogInformation(
                "Портал: «{Name}» — эпизодов {Count}, interactive={Interactive}, подписчиков выбора {Subscribers}.",
                channel.Name, flick.Episodes.Count, interactive, PortalEpisodePickRequested?.GetInvocationList().Length ?? 0);
            if (flick.Episodes.Count > 1 && interactive && PortalEpisodePickRequested is { } pick)
            {
                var chosen = await pick(channel, flick);
                if (chosen is not { } picked)
                {
                    // Пользователь закрыл диалог выбора серии — не играем ничего.
                    return false;
                }

                // Сезон в диалоге могли сменить — играем выбранную карточку
                // и её список серий.
                channel = picked.Channel;
                flick = new PortalFlickResult
                {
                    SerialTitle = picked.Channel.Name,
                    Description = picked.Channel.Description,
                    PosterUrl = picked.Channel.LogoUrl,
                    Episodes = picked.Episodes
                };
                episode = picked.Episode;
            }

            // Стартовое качество — из настроек приложения (PreferredQuality:
            // 0 = авто), если такой вариант у портала есть.
            var preferred = AppSettings.PreferredQuality > 0 ? AppSettings.PreferredQuality + "p" : "Авто";
            var quality = episode.Variants.Count > 0 ? preferred : null;

            var episodeResume = interactive
                ? await OfferVodResumeAsync(channel.Name, flick.Episodes.IndexOf(episode))
                : null;
            await Player.StartPlaybackAsync(channel, episode.StreamUrl, archiveEntry: null, isVod: true,
                vodVariants: episode.Variants, vodQuality: quality,
                resumePosition: episodeResume,
                vodEpisodes: flick.Episodes, vodEpisodeIndex: flick.Episodes.IndexOf(episode));
            return true;
        }

        await Player.PlayLiveAsync(channel);
        return !string.IsNullOrWhiteSpace(channel.StreamUrl);
    }

    /// <summary>
    /// Фоновая догрузка вариантов качества для фильма, стартовавшего
    /// мгновенно по ссылке каталога: результат подкладывается в играющий
    /// плеер (SetVodVariants), если пользователь ещё на этом фильме.
    /// </summary>
    private async Task LoadPortalVariantsInBackgroundAsync(Models.PlaylistSource playlist, ChannelViewModel channel)
    {
        try
        {
            var flick = await _videoPortalService.ResolveEpisodesAsync(playlist, channel.PortalRequest!);
            if (Player.IsVodPlaying && ReferenceEquals(Player.VodChannel, channel) &&
                flick.Episodes.Count > 0)
            {
                Player.SetVodVariants(flick.Episodes[0].Variants);
            }
        }
        catch (Exception ex)
        {
            // Не критично: фильм уже играет по ссылке каталога, просто без
            // выбора качества (кнопка не появится).
            _logger.LogDebug(ex, "Фоновая догрузка вариантов качества для «{Item}» не удалась.", channel.Name);
        }
    }

    /// <summary>
    /// Представление показывает диалог выбора серии (MainPage →
    /// EpisodePickerDialog) и возвращает выбранную пару сезон/эпизод;
    /// null — отменено.
    /// </summary>
    public event Func<ChannelViewModel, PortalFlickResult, Task<(ChannelViewModel Channel, PortalEpisode Episode, System.Collections.Generic.List<PortalEpisode> Episodes)?> >? PortalEpisodePickRequested;

    // ===================== EPG =====================

    /// <summary>Показ/скрытие EPG-оверлея.</summary>
    [RelayCommand]
    private void ToggleEpg()
    {
        IsEpgVisible = !IsEpgVisible;
        EpgVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    // ===================== Сохранение настроек =====================

    /// <summary>Непосредственное сохранение канонической копии настроек.</summary>
    public async Task SaveSettingsAsync()
    {
        try
        {
            await _settingsService.SaveAsync(AppSettings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveSettingsAsync: не удалось сохранить настройки.");
        }
    }
}
