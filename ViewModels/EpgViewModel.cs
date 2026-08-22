using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using IptvPlayer.Converters;
using IptvPlayer.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.ViewModels;

public partial class EpgViewModel : ObservableObject
{
    private static readonly TimeSpan BackwardSpan = TimeSpan.FromHours(72);
    private static readonly TimeSpan ForwardSpan = TimeSpan.FromHours(48);
    private const double PixelsPerHour = 100;

    private readonly IEPGService _epgService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<EpgViewModel> _logger;
    private DateTime _windowStart;
    private List<int> _timeScaleHours = new();
    private bool _isLoading;
    private readonly System.Threading.SemaphoreSlim _loadLock = new(1, 1);

    public EpgViewModel(IEPGService epgService, ISettingsService settingsService, ILogger<EpgViewModel> logger)
    {
        _epgService = epgService;
        _settingsService = settingsService;
        _logger = logger;
        ResetWindowToNow();
        LoadEpgSourcesFromSettings();
    }

    // Простые свойства — [ObservableProperty]. Свойства с приватными
    // сеттерами и побочными эффектами (WindowStart, TimeScaleHours,
    // IsLoading) ниже оставлены ручными: генератор делает сеттер публичным,
    // а здесь важны инкапсуляция и логика внутри сеттера.
    // Ручные INotifyPropertyChanged-свойства (SetProperty): сгенерированные
    // [ObservableProperty] в WinUI-сценариях не создают WinRT-проекторов
    // (MVVMTK0045). Ниже — простые версии; свойства с приватными сеттерами
    // и побочными эффектами (WindowStart, TimeScaleHours, IsLoading) — в
    // ручном стиле ещё ниже.
    private ObservableCollection<ChannelViewModel> _channels = new();

    public ObservableCollection<ChannelViewModel> Channels
    {
        get => _channels;
        set => SetProperty(ref _channels, value);
    }

    private ObservableCollection<ChannelViewModel> _filteredChannels = new();

    public ObservableCollection<ChannelViewModel> FilteredChannels
    {
        get => _filteredChannels;
        set => SetProperty(ref _filteredChannels, value);
    }

    private string _epgSource = string.Empty;

    public string EpgSource
    {
        get => _epgSource;
        set => SetProperty(ref _epgSource, value);
    }

    private List<string> _ePGSources = new();

    public List<string> EPGSources
    {
        get => _ePGSources;
        set => SetProperty(ref _ePGSources, value);
    }

    private string? _selectedEPGSource;

    public string? SelectedEPGSource
    {
        get => _selectedEPGSource;
        set => SetProperty(ref _selectedEPGSource, value);
    }

    /// <summary>
    /// Начало общей 120-часовой шкалы (now-72h..now+48h), а не выбранный
    /// календарный день — раньше EPG грузилось постранично по дням, теперь
    /// весь диапазон грузится целиком и дальше просто скроллится.
    /// </summary>
    public DateTime WindowStart
    {
        get => _windowStart;
        private set
        {
            if (_windowStart != value)
            {
                _windowStart = value;
                EpgTimelineScale.WindowStart = value;
                EpgTimelineScale.PixelsPerHour = PixelsPerHour;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WindowEnd));
                RecalculateTimeScaleHours();
                RebindTimelineEntries();
            }
        }
    }

    public DateTime WindowEnd => WindowStart + BackwardSpan + ForwardSpan;

    /// <summary>
    /// Час суток (0-23) для каждого часового столбца шкалы над сеткой EPG —
    /// раньше был статичным списком 0..23 на один календарный день, теперь
    /// один элемент на каждый час 120-часового окна [WindowStart..WindowEnd],
    /// т.к. шкала теперь не постраничная, а сплошная.
    /// </summary>
    public List<int> TimeScaleHours
    {
        get => _timeScaleHours;
        private set
        {
            if (_timeScaleHours != value)
            {
                _timeScaleHours = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Показывает индикатор загрузки над сеткой EPG (ProgressBar в MainPage.xaml) —
    /// раньше во время LoadEPGAsync/RefreshEPGAsync не было никакой обратной
    /// связи, что вообще что-то происходит.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading != value)
            {
                _isLoading = value;
                OnPropertyChanged();
            }
        }
    }

    // LoadEPGAsync и RefreshEPGAsync могут перекрываться (первая настройка:
    // фоновая загрузка из диалога настроек ещё идёт, а «Готово» с изменёнными
    // источниками запускает Refresh поверх). Раньше каждый метод в finally
    // безусловно писал IsLoading = false — завершившийся ПЕРВЫМ гасил
    // индикатор, пока вторая операция ещё качала/разбирала XMLTV: полоса
    // пропадала, а EPG появлялся только по завершении оставшейся операции
    // (или не появлялся вовсе, если та падала и исключение глотал
    // fire-and-forget). Счётчик гасит индикатор только когда не осталось
    // ни одной активной операции.
    private int _activeLoadOperations;

    private void BeginLoadOperation()
    {
        _activeLoadOperations++;
        IsLoading = true;
    }

    private void EndLoadOperation()
    {
        if (_activeLoadOperations > 0)
        {
            _activeLoadOperations--;
        }

        if (_activeLoadOperations == 0)
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// EPG (пере)загружен и RecalculateCurrentProgramsAsync обновил
    /// CurrentProgramTitle/логотипы каналов. Подписчики (MainPageViewModel)
    /// пересобирают список каналов: логотипы заполняются в том числе из
    /// фонового потока (ApplyMissingLogosAsync внутри Task.Run в
    /// RefreshEPGAsync), где уведомления INPC могли не дойти до привязок —
    /// пересоздание DisplayedChannels перечитывает все привязки на UI-потоке.
    /// </summary>
    public event EventHandler? EpgReloaded;



    private async void LoadEpgSourcesFromSettings()
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            var activeSources = settings.GetActiveEpgSources();
            var enabledSources = activeSources.Where(s => s.IsEnabled).ToList();

            // Если нет включённых источников, но есть в настройках отключённые — включаем первый рабочий (epg.one)
            if (enabledSources.Count == 0 && activeSources.Count > 0)
            {
                var fallback = activeSources.FirstOrDefault(s => s.Url.Contains("epg.one"));
                if (fallback != null)
                {
                    fallback.IsEnabled = true;
                    await _settingsService.SaveAsync(settings);
                    enabledSources.Add(fallback);
                }
            }

            EPGSources = enabledSources.Select(s => s.Url).ToList();
            if (EPGSources.Count > 0)
            {
                SelectedEPGSource = EPGSources.First();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadEpgSourcesFromSettings: не удалось загрузить источники EPG из настроек.");
        }
    }

    public void SetChannels(IEnumerable<ChannelViewModel> channels)
    {
        Channels.Clear();
        foreach (var channel in channels)
        {
            Channels.Add(channel);
        }
        ApplyFilter();
    }

    public void ApplyFilter(string? query = null)
    {
        FilteredChannels.Clear();
        var filtered = string.IsNullOrEmpty(query)
            ? Channels
            : Channels.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var channel in filtered)
        {
            FilteredChannels.Add(channel);
        }
    }

    public void ApplyEPGSource()
    {
        if (SelectedEPGSource != null)
        {
            EpgSource = SelectedEPGSource;
        }
    }

    public async Task LoadEPGAsync()
    {
        // Раньше два перекрывающихся вызова (например, "Обновить EPG" ещё не
        // закончился, а следом кликнули "Сегодня" и выбрали другой канал)
        // могли одновременно попасть в SetChannels (Channels.Clear()/Add())
        // и в foreach по Channels внутри RecalculateCurrentProgramsAsync —
        // это InvalidOperationException "Collection was modified", который
        // и приводил к зависанию/крэшу. Семафор сериализует такие вызовы.
        await _loadLock.WaitAsync();
        try
        {
            BeginLoadOperation();
            var channels = await _epgService.GetChannelsAsync();
            SetChannels(channels);
            await RecalculateCurrentProgramsAsync();
            EpgReloaded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadEPGAsync: не удалось загрузить EPG.");
            throw;
        }
        finally
        {
            EndLoadOperation();
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Полная перезагрузка EPG при переключении активного плейлиста: сервис
    /// перечитывает источники уже нового плейлиста (без очистки дискового
    /// кэша), затем пересобираются программы каналов. Под тем же семафором,
    /// что и LoadEPGAsync.
    /// </summary>
    public async Task ReloadForPlaylistAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            BeginLoadOperation();
            await _epgService.ReloadSourcesAsync();
            var channels = await _epgService.GetChannelsAsync();
            SetChannels(channels);
            await RecalculateCurrentProgramsAsync();
            EpgReloaded?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            EndLoadOperation();
            _loadLock.Release();
        }
    }

    public async Task LoadEPGForChannelAsync(int channelId)
    {
        // Тот же семафор, что и в LoadEPGAsync — иначе клик по каналу во
        // время ещё не завершённого Refresh/Today мог столкнуться с
        // RecalculateCurrentProgramsAsync на EPGEntries того же канала.
        await _loadLock.WaitAsync();
        try
        {
            var entries = await _epgService.GetEPGEntriesAsync(channelId);
            var channel = Channels.FirstOrDefault(c => c.Id == channelId);
            if (channel == null)
            {
                return;
            }

            channel.EPGEntries.Clear();
            foreach (var entry in entries)
            {
                entry.IsCurrent = false;
                channel.EPGEntries.Add(entry);
            }

            var now = DateTime.Now;
            var current = entries.FirstOrDefault(e => e.StartTime <= now && now < e.EndTime);
            if (current != null)
            {
                current.IsCurrent = true;
                channel.CurrentProgramTitle = current.ProgramName;
                channel.CurrentEPGEntry = current;
            }
            else
            {
                channel.CurrentProgramTitle = string.Empty;
                channel.CurrentEPGEntry = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadEPGForChannelAsync: не удалось загрузить EPG канала {ChannelId}.", channelId);
            throw;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task RefreshEPGAsync()
    {
        BeginLoadOperation();
        try
        {
            // _epgService.RefreshEPGAsync() скачивает и разбирает XMLTV по всем
            // источникам — потенциально тяжёлая по CPU (парсинг XML) и по I/O
            // работа. Раньше она выполнялась прямо в продолжении await на
            // UI-потоке: пока метод не завершится, UI-поток занят и не может
            // ни отрисовывать новые кадры видео в MediaPlayerElement, ни
            // обрабатывать ввод — визуально это выглядит как "видео подвисло".
            // Аудио при этом действительно продолжает идти, потому что
            // декодирование и воспроизведение звука у MediaPlayer идёт на
            // отдельном, не-UI потоке и от UI-потока не зависит.
            // Переносим это на пул потоков через Task.Run — сам метод
            // безопасен для вызова не с UI-потока, так как ничего не трогает
            // напрямую в UI (ObservableCollection здесь не меняется —
            // это делает LoadEPGAsync ниже, уже на UI-потоке).
            await Task.Run(() => _epgService.RefreshEPGAsync());
            await LoadEPGAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RefreshEPGAsync: не удалось обновить EPG.");
            throw;
        }
        finally
        {
            EndLoadOperation();
        }
    }

    private void ResetWindowToNow()
    {
        WindowStart = DateTime.Now - BackwardSpan;
    }

    private void RecalculateTimeScaleHours()
    {
        var totalHours = (int)(BackwardSpan + ForwardSpan).TotalHours;
        TimeScaleHours = Enumerable.Range(0, totalHours)
            .Select(offset => WindowStart.AddHours(offset).Hour)
            .ToList();
    }

    /// <summary>
    /// Раньше CurrentProgramTitle обновлялся только по клику на канал
    /// (LoadEPGForChannelAsync). Теперь пересчитывается сразу для всех
    /// каналов при каждой загрузке/обновлении EPG.
    /// </summary>
    /// <summary>
    /// Раньше цикл на все каналы (2065 штук) выполнялся одним синхронным
    /// блоком: GetEPGEntriesAsync внутри не делает настоящего I/O (только
    /// Dictionary-lookup в памяти), поэтому await по нему почти всегда
    /// завершается синхронно, а в этом случае компилятор НЕ отдаёт
    /// управление обратно в message loop UI-потока — продолжение цикла
    /// выполняется тут же, инлайново. В результате весь foreach шёл единым
    /// непрерывным куском: ни перерисовки кадра, ни обработки клика мыши,
    /// пока не закончится весь список — визуально это выглядело как
    /// "прогресс крутится, а приложение не отвечает", а не просто "долго".
    /// Task.Yield() каждые YieldEveryNChannels итераций форсированно
    /// возвращает управление в UI message loop между пачками — интерфейс
    /// остаётся отзывчивым (клики/отрисовка обрабатываются), при этом
    /// общее время работы почти не меняется (Task.Yield — это одна
    /// операция постановки в очередь диспетчера, не реальный I/O).
    /// </summary>
    private const int YieldEveryNChannels = 50;

    private async Task RecalculateCurrentProgramsAsync()
    {
        var now = DateTime.Now;
        var processed = 0;

        foreach (var channel in Channels)
        {
            var entries = await _epgService.GetEPGEntriesAsync(channel.Id);

            channel.EPGEntries.Clear();
            foreach (var entry in entries)
            {
                entry.IsCurrent = false;
                channel.EPGEntries.Add(entry);
            }

            var current = entries.FirstOrDefault(e => e.StartTime <= now && now < e.EndTime);
            channel.CurrentProgramTitle = current?.ProgramName ?? string.Empty;
            channel.CurrentEPGEntry = current;
            if (current != null)
            {
                current.IsCurrent = true;
                current.RefreshLiveProgress();
            }

            // Полоса прогресса в строке канала течёт со временем — уведомляем
            // каждый проход, даже если передача не сменилась.
            channel.RefreshCurrentProgramProgress();

            processed++;
            if (processed % YieldEveryNChannels == 0)
            {
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Облегчённое обновление "текущей передачи" для ВСЕХ каналов — только
    /// CurrentProgramTitle/CurrentEPGEntry/IsCurrent, без пересборки
    /// EPGEntries-коллекций. Вызывается таймером из MainPage каждую минуту:
    /// строка в списке каналов не должна застревать на передаче, актуальной
    /// на момент загрузки плеера (раньше обновлялась только по клику на
    /// канал). INotifyPropertyChanged у ChannelViewModel обновляет список
    /// без его пересоздания (в шаблоне стоит Mode=OneWay).
    /// </summary>
    public async Task RefreshCurrentProgramsLightAsync()
    {
        if (IsLoading)
        {
            // Полная загрузка/обновление EPG идёт прямо сейчас — она сама
            // всё пересчитает в конце (RecalculateCurrentProgramsAsync).
            return;
        }

        var now = DateTime.Now;
        var processed = 0;

        foreach (var channel in Channels)
        {
            var entries = await _epgService.GetEPGEntriesAsync(channel.Id);
            var current = entries.FirstOrDefault(e => e.StartTime <= now && now < e.EndTime);

            if (!string.Equals(channel.CurrentProgramTitle, current?.ProgramName ?? string.Empty, StringComparison.Ordinal))
            {
                // Снять признак с прежней текущей и поставить новой — чтобы
                // подсветка и полоса прогресса в панели передач не врали
                // при следующей отрисовке.
                if (channel.CurrentEPGEntry != null)
                {
                    channel.CurrentEPGEntry.IsCurrent = false;
                }
                if (current != null)
                {
                    current.IsCurrent = true;
                }

                channel.CurrentProgramTitle = current?.ProgramName ?? string.Empty;
                channel.CurrentEPGEntry = current;
            }

            // Полосы прогресса (строка канала и карточка текущей передачи в
            // EPG) привязаны к часам — обновляются каждые 30 с этим же
            // таймером, даже когда текущая передача не менялась.
            channel.RefreshCurrentProgramProgress();
            current?.RefreshLiveProgress();

            processed++;
            if (processed % YieldEveryNChannels == 0)
            {
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// EPGEntry не реализует INotifyPropertyChanged, так что смена
    /// EpgTimelineScale.WindowStart сама по себе не пересчитает уже
    /// забинженные Canvas.Left/Width в сетке. Пересобираем коллекции —
    /// это форсирует переконвертацию всех биндингов на новую WindowStart.
    /// </summary>
    private void RebindTimelineEntries()
    {
        foreach (var channel in Channels)
        {
            var entries = channel.EPGEntries.ToList();
            channel.EPGEntries.Clear();
            foreach (var entry in entries)
            {
                channel.EPGEntries.Add(entry);
            }
        }
    }

    // ===================== Команды =====================
    // Постраничная навигация PrevDay/NextDay/Today умерла после перехода со
    // смены дней на сплошное 120-часовое окно (список просто скроллится) —
    // команды и методы NavigateDate/NavigateToToday/_dayOffset/CurrentDate
    // удалены как неиспользуемые. Осталась только принудительная перезагрузка.

    /// <summary>
    /// Принудительное обновление EPG (кнопка в пустом состоянии панели).
    /// </summary>
    [RelayCommand]
    private async Task RefreshEpgAsync()
    {
        if (IsLoading)
        {
            return;
        }
        await RefreshEPGAsync();
    }
}
