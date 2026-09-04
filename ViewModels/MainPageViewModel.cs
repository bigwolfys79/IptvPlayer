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
    private static string AllGroupsOption => L.T("Vse_Gruppy");
    private static string AllGenresOption => L.T("Vse_Zhanry");
    private static string AllYearsOption => L.T("Vse_Gody");
    private static string FavoritesOption => L.T("Izbrannoe");

    private readonly ISettingsService _settingsService;
    private readonly IVideoPortalService _videoPortalService;
    private readonly Services.VodResumeStore _vodResumeStore;

    /// <summary>Позиции досмотра VOD — в кэш-БД, не в settings.json.</summary>
    private readonly Dictionary<string, VodResumePosition> _vodResumePositions = new();
    private readonly ILogger<MainPageViewModel> _logger;

    // Все свойства ниже — ручные (поле + SetProperty) вместо [ObservableProperty]:
    // сгенерированные генератором в WinUI-сценариях не создают WinRT-проекторов
    // (предупреждение MVVMTK0045), а семантика INotifyPropertyChanged та же.
    private EpgViewModel _epgViewModel;

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

    private string _selectedYear = "";

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

    private static string AllContentTypesOption => L.T("Vse_Tipy");

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
        Services.VodResumeStore vodResumeStore,
        ILogger<MainPageViewModel> logger)
    {
        _epgViewModel = epgViewModel;
        _settingsService = settingsService;
        _videoPortalService = videoPortalService;
        _vodResumeStore = vodResumeStore;
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
        // Дневной лимит просмотра: исчерпан — запуск запрещён до полуночи
        // (независимо от PIN: разблокировка групп снимает скрытие, но не лимит).
        if (ParentalControlService.IsDailyLimitReached(AppSettings, DateTime.Now))
        {
            _logger.LogInformation(
                "Дневной лимит просмотра исчерпан — канал {Channel} не запущен.", channel.Name);
            DailyLimitBlocked?.Invoke(this, EventArgs.Empty);
            return false;
        }

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

    // ===================== Дневной лимит просмотра =====================

    /// <summary>Лимит исчерпан во время просмотра — остановить воспроизведение.</summary>
    public event EventHandler? DailyLimitReached;

    /// <summary>Попытка запуска при исчерпанном лимите — показать сообщение.</summary>
    public event EventHandler? DailyLimitBlocked;

    /// <summary>«Лимит уже объявлен» — событие остановки поднимается один раз за день.</summary>
    private bool _dailyLimitAnnounced;

    /// <summary>Накопленные, но ещё не записанные на диск секунды просмотра.</summary>
    private int _unsavedWatchedSeconds;

    /// <summary>
    /// Учёт секунды активного просмотра (вызывается из секундного таймера
    /// code-behind, когда плеер реально играет). Запись настроек на диск —
    /// раз в минуту накопления, чтобы не писать файл каждую секунду.
    /// </summary>
    public void AddPlaybackWatchTime(int seconds)
    {
        var settings = AppSettings;
        if (!settings.ParentalControlEnabled || settings.ParentalDailyLimitMinutes <= 0)
        {
            return;
        }

        var localNow = DateTime.Now;
        ParentalControlService.AddWatchedSeconds(settings, seconds, localNow);
        _unsavedWatchedSeconds += seconds;
        if (_unsavedWatchedSeconds >= 60)
        {
            _unsavedWatchedSeconds = 0;
            SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
        }

        var reached = ParentalControlService.IsDailyLimitReached(settings, DateTime.Now);
        if (reached && !_dailyLimitAnnounced)
        {
            _dailyLimitAnnounced = true;
            _logger.LogInformation("Дневной лимит просмотра ({Minutes} мин) исчерпан — остановка воспроизведения.",
                settings.ParentalDailyLimitMinutes);
            DailyLimitReached?.Invoke(this, EventArgs.Empty);
        }
        else if (!reached)
        {
            // Лимит снят (смена настроек) или наступил новый день — снова можно объявлять.
            _dailyLimitAnnounced = false;
        }
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

        // Избранные — наверху списка; порядок остальных — как в источнике.
        filtered = filtered.OrderByDescending(c => c.IsFavorite);

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
        ChannelCountText = string.Format(L.T("Kanalov_0"), Channels.Count, Channels.Count);
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
            ArchivePlayErrorRequested?.Invoke(this, L.T("U_Kanala_Net_URL_Potoka_Arkhiv"));
            return;
        }

        if (!await EnsureChannelAllowedAsync(channel))
        {
            return;
        }

        if (entry.StartTime > DateTime.Now)
        {
            ArchivePlayErrorRequested?.Invoke(this, L.T("Eta_Peredacha_Eshche_Ne_Nachalas"));
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

    /// <summary>
    /// Запуск канала/фильма: обычный канал — прямой эфир как раньше; элемент
    /// портала — воспроизведение в режиме VOD (пауза без рестарта потока).
    /// </summary>
    public async Task<bool> PlayChannelAsync(ChannelViewModel channel)
        => await PlayChannelAsync(channel, interactive: true);

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
            // ConfigureAwait(false): Closed-хук окна вызывает SaveSettingsAsync
            // через GetResult на UI-потоке — продолжение на Dispatcher дедлочит.
            await _settingsService.SaveAsync(AppSettings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveSettingsAsync: не удалось сохранить настройки.");
        }
    }
}
