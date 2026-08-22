using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.ViewModels;

/// <summary>
/// Корневая ViewModel страницы (этап 2 MVVM): списки каналов, выбранный канал,
/// вложенные ViewModel и команды — избранное, напоминания, расписание записей,
/// запись канала, пауза архива, возврат к эфиру, показ/скрытие EPG, фильтрация.
/// </summary>
public partial class MainPageViewModel : ObservableObject
{
    private const string AllGroupsOption = "Все группы";
    private const string FavoritesOption = "★ Избранное";

    private readonly ISettingsService _settingsService;
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
        PlayerViewModel player,
        RecordingService recording,
        ILogger<MainPageViewModel> logger)
    {
        _epgViewModel = epgViewModel;
        _settingsService = settingsService;
        _logger = logger;
        Player = player;
        Recording = recording;
        _selectedChannel = new ChannelViewModel(); // избегаем null для x:Bind путей

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

    private void OnSearchQueryChanged(string value) => FilterChannels();

    private void OnSelectedGroupChanged(string value) => FilterChannels();

    private void OnChannelsChanged(ObservableCollection<ChannelViewModel> value)
    {
        RefreshGroups();
        FilterChannels();
        UpdateChannelCountText();
    }

    // ===================== Фильтрация каналов =====================

    /// <summary>
    /// Пересчитывает DisplayedChannels с учётом текста поиска и выбранной группы.
    /// Избранные каналы всегда стоят первыми (OrderingDescending по IsFavorite —
    /// стабильная сортировка сохраняет исходный порядок внутри каждой части).
    /// </summary>
    public void FilterChannels()
    {
        var query = SearchQuery?.Trim() ?? string.Empty;
        var selectedGroup = SelectedGroup;

        IEnumerable<ChannelViewModel> filtered = Channels;

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(selectedGroup) && selectedGroup == FavoritesOption)
        {
            filtered = filtered.Where(c => c.IsFavorite);
        }
        else if (!string.IsNullOrEmpty(selectedGroup) && selectedGroup != AllGroupsOption)
        {
            filtered = filtered.Where(c => string.Equals(c.Group?.Trim(), selectedGroup, StringComparison.OrdinalIgnoreCase));
        }

        // Избранные — наверху списка при любом фильтре.
        filtered = filtered.OrderByDescending(c => c.IsFavorite);

        // Clear() затирает выделение ListView (SelectedItem TwoWay уходит в
        // null), а с ним SelectedChannel — на него завязана видимость видео
        // (SelectedChannel.IsPlaying у MediaPlayerElement): получался
        // «канал играет, но только звук». Если выбранный канал остался в
        // отфильтрованном списке — возвращаем выбор.
        var selected = SelectedChannel;

        DisplayedChannels.Clear();
        foreach (var channel in filtered)
        {
            DisplayedChannels.Add(channel);
        }

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
        await Player.PlayLiveAsync(channel);

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

            if (Recording.IsActive)
            {
                // Занято другой записью — попробуем на следующем тике.
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
                remaining);

            if (started != null)
            {
                AppSettings.ScheduledRecordings.RemoveAt(i);
                changed = true;
            }
        }

        if (changed)
        {
            SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
            ApplyReminderFlags();
            IsRecording = Recording.IsActive;
            RecordingChanged?.Invoke(this, EventArgs.Empty);
        }
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

        if (Recording.IsActive)
        {
            Recording.Stop();
        }
        else
        {
            var channel = SelectedChannel;
            if (channel == null || string.IsNullOrWhiteSpace(channel.StreamUrl))
            {
                return;
            }

            var path = Recording.Start(channel.StreamUrl, channel.Name, durationSec: null);
            if (path == null)
            {
                RecordError = L.T(
                    "Не удалось начать запись (ffmpeg недоступен или запись уже идёт) — см. лог.",
                    "Could not start recording (ffmpeg missing or already recording) — see log.");
            }
        }

        IsRecording = Recording.IsActive;
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

    /// <summary>Возврат из архива к прямому эфиру выбранного канала.</summary>
    [RelayCommand]
    private async Task BackToLiveAsync()
    {
        var channel = SelectedChannel;
        if (channel != null)
        {
            await Player.PlayLiveAsync(channel);
        }
    }

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
