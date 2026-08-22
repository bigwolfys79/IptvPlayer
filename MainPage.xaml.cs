using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.Controls;
using IptvPlayer.ViewModels;
using Windows.System;
using Windows.UI.Core;
// Windows.Media.Playback.MediaPlayer конфликтует по имени с x:Name="MediaPlayer"
// (MediaPlayerElement) в разметке, поэтому в коде тип всегда указывается
// с полным неймспейсом: Windows.Media.Playback.MediaPlayer.

namespace IptvPlayer;

/// <summary>
/// The main content page displayed inside the application window.
/// Contains the channel list, EPG display, and media player controls.
/// </summary>
public sealed partial class MainPage : Page
{
    private const string AllGroupsOption = "Все группы";
    private const string FavoritesOption = "★ Избранное";

    // Все сервисы и ViewModel резолвятся в конструкторе из DI-контейнера
    // App.Services (WinUI не даёт внедрять зависимости в конструкторы
    // XAML-элементов). Раньше каждый сервис создавался здесь же через new
    // (и ещё в шести местах по коду — выходили разные экземпляры
    // SettingsService).
    private readonly IM3UParserService _m3uParserService;
    private readonly ISettingsService _settingsService;
    private readonly IPlaylistCacheService _playlistCacheService;

    /// <summary>
    /// Активный плейлист (AppSettings.Playlists по ActivePlaylistId) — каналы
    /// в списке принадлежат ему; переключение — SwitchPlaylistAsync.
    /// </summary>
    private PlaylistSource? _activePlaylist;
    private readonly IStreamService _streamService;
    private readonly ChannelRepository _channelRepository;
    private readonly ILogger<MainPage> _logger;

    // Раньше здесь и в InitializeAsync() создавались ДВА разных EpgViewModel:
    // временный (в инициализаторе свойства ViewModel, с одноразовым
    // ChannelRepository "в никуда") и настоящий (в InitializeAsync, с
    // _channelRepository), который затем подставлялся в
    // ViewModel.EpgViewModel = new EpgViewModel(epgService). Похоже, именно
    // эта подмена объекта, на который уже забинжены несколько x:Bind-путей
    // разом (CurrentDate/EPGSources/IsLoading/TimeScaleHours/FilteredChannels,
    // включая TwoWay на SelectedEPGSource), приводила к NullReferenceException
    // внутри автогенерированного Update_ViewModel_EpgViewModel — сгенерированный
    // код x:Bind не всегда переживает замену промежуточного объекта в пути
    // биндинга. Теперь EPGService и EpgViewModel создаются один раз, сразу с
    // правильным _channelRepository, и никогда не подменяются — только их
    // содержимое (Channels/FilteredChannels и т.п.) обновляется по месту.
    private readonly EPGService _epgService;

    // Измеренная скорость загрузки (байты чтения процесса) для оверлея
    // статистики — см. ProcessSpeedMonitor.
    private readonly ProcessSpeedMonitor _speedMonitor;

    // Плеер/запись/состояние EPG живут в ViewModel (этап 2 MVVM); короткий
    // алиас для читаемости в оставшемся коде представления.
    private PlayerViewModel Player => ViewModel.Player;

    // Защита от ложного "пользователь поменял громкость": программная
    // синхронизация слайдера с громкостью плеера при входе в fullscreen
    // тоже вызывает ValueChanged, но не должна трогать Player.LastUserVolume.
    private bool _isVolumeSliderSyncing;

    // EPG-панель теперь перекрывающий оверлей поверх видео и по умолчанию
    // СКРЫТА — открывается кнопкой EPG в панели управления видео.
    private bool _isFullScreen = false;
    private bool _wasEpgVisibleBeforeFullScreen = false;
    private double _channelListExpandedWidth = 320;

    // Автоскрытие оверлея (список каналов, кнопки плеера, EPG/выход) в fullscreen:
    // таймер сбрасывается при каждом движении мыши и прячет оверлей по истечении.
    private readonly DispatcherTimer _overlayHideTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    // Каноническая копия настроек — теперь живёт в ViewModel (этап 2 MVVM).
    // Код представления обращается через ViewModel.AppSettings.

    // Дебаунс записи настроек (избранное, последний канал, напоминания).
    private readonly DispatcherTimer _settingsSaveDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    // Проверка напоминаний о передачах (тосты Windows).
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    // Запись каналов/передач через ffmpeg.exe (одна активная запись).
    private bool _toastFailureLogged;

    // Периодическое обновление "текущей передачи" во всём списке каналов:
    // без него строка оставалась той, что была на момент загрузки плеера,
    // и менялась только по клику на канал (см. RefreshCurrentProgramsLightAsync).
    // 30 с (а не минута) — заодно ходят полосы прогресса передач в списке
    // каналов и в карточках EPG.
    private readonly DispatcherTimer _currentProgramRefreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    // Секундный тик полосы перемотки архива: позиция считается по стенным
    // часам в PlayerViewModel и толкается в слайдеры обеих панелей. Вне
    // архивного воспроизведения — тихий no-op.
    private readonly DispatcherTimer _archivePositionTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Дебаунс коммита перемотки архива: ValueChanged сыплется на каждый шевел
    // ползунка — перематываем не раньше, чем 600 мс после последнего
    // изменения. Это же страховка от событий указателя: PointerCaptureLost
    // у WinUI-слайдера не гарантирован (захват может жить на внутреннем
    // Thumb и не дойти до слайдера), а ValueChanged приходит всегда.
    private readonly DispatcherTimer _archiveSeekDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    // Слайдер, который пользователь тянет прямо сейчас (оконный или
    // полноэкранный), и защита от программных присваиваний Value.
    private Slider? _activeSeekSlider;
    private bool _updatingSeekBarValue;

    // Дебаунс записи громкости в settings.json (см. конструктор).
    private readonly DispatcherTimer _volumeSaveDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    // Последняя позиция указателя над видео в ОКОННОМ режиме — для защиты от
    // "синтетических" PointerMoved с той же координатой, когда под неподвижным
    // курсором появляется/исчезает сам WindowedVideoOverlay (аналогично
    // _lastOverlayPointerPosition для полноэкранного оверлея).
    private Windows.Foundation.Point _lastWindowedOverlayPointerPosition = new(-1, -1);

    public MainPageViewModel ViewModel { get; }

    public MainPage()
    {
        // Composition через DI-контейнер: MainPage — первый XAML-элемент,
        // которому нужны сервисы и ViewModel'ы, дальше они расходятся по
        // дереву (SettingsDialog и т.п.). Всё создаётся лениво здесь, на
        // UI-потоке — EPGService в конструкторе захватывает его
        // DispatcherQueue.
        var services = App.Services;
        _m3uParserService = services.GetRequiredService<IM3UParserService>();
        _settingsService = services.GetRequiredService<ISettingsService>();
        _playlistCacheService = services.GetRequiredService<IPlaylistCacheService>();
        _streamService = services.GetRequiredService<IStreamService>();
        _channelRepository = services.GetRequiredService<ChannelRepository>();
        _epgService = services.GetRequiredService<EPGService>();
        _speedMonitor = services.GetRequiredService<ProcessSpeedMonitor>();
        _logger = services.GetRequiredService<ILogger<MainPage>>();
        ViewModel = services.GetRequiredService<MainPageViewModel>();

        // Мосты «ViewModel → представление» (этап 2 MVVM): VM меняет состояние,
        // страница реагирует чисто визуальными действиями.
        Player.PlayerChanged += (s, e) =>
        {
            // На UI-потоке — синхронно: PlayerViewModel.Stop() сразу после
            // события освобождает старый плеер, и отложенный через
            // TryEnqueue SetMediaPlayer(null) выполнялся уже после Dispose —
            // медиа-движок доставал освобождённый плеер, процесс падал
            // при переключении канала.
            if (DispatcherQueue.HasThreadAccess)
            {
                MediaPlayer.SetMediaPlayer(Player.Player);
            }
            else
            {
                DispatcherQueue.TryEnqueue(() => MediaPlayer.SetMediaPlayer(Player.Player));
            }
        };
        Player.ArchiveStateChanged += (s, e) =>
            DispatcherQueue.TryEnqueue(UpdateArchiveBanner);
        ViewModel.RecordingChanged += (s, e) =>
            DispatcherQueue.TryEnqueue(UpdateRecordButtons);
        ViewModel.EpgVisibilityChanged += (s, e) =>
            DispatcherQueue.TryEnqueue(ApplyEpgVisibility);
        Player.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is not (nameof(PlayerViewModel.IsBuffering) or nameof(PlayerViewModel.StreamError)))
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                BufferProgress.Visibility = Player.IsBuffering ? Visibility.Visible : Visibility.Collapsed;
                StreamErrorText.Text = Player.StreamError ?? string.Empty;
                StreamErrorText.Visibility = string.IsNullOrEmpty(Player.StreamError)
                    ? Visibility.Collapsed
                    : Visibility.Visible;

                // Ошибка потока гасит индикатор воспроизведения у канала, если
                // он ещё актуален (раньше это делал MediaFailed в code-behind).
                if (!string.IsNullOrEmpty(Player.StreamError) &&
                    ViewModel.SelectedChannel != null &&
                    Player.CurrentPlayerChannelId == ViewModel.SelectedChannel.Id)
                {
                    ViewModel.SelectedChannel.IsPlaying = false;
                }
            });
        };
        // Смена беззвучного режима: кнопки M в панелях и слайдеры (показывают
        // ноль, громкость хранится в PlayerViewModel и не затирается).
        Player.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.IsMuted))
            {
                DispatcherQueue.TryEnqueue(UpdateMuteButtons);
            }
        };

        // Простои буфера для оверлея статистики: каждый BufferingStarted
        // текущего плеера — «затык» воспроизведения. Счётчик и таймер сессии
        // сбрасываются при смене плеера (PlayerChanged).
        Player.PlayerChanged += (s, e) =>
        {
            _bufferingStallCount = 0;
            if (Player.Player != null)
            {
                _channelSessionStartUtc = DateTime.UtcNow;
                Player.Player.BufferingStarted += (ps, pe) =>
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _bufferingStallCount++;
                        UpdateStatsOverlay();
                    });
            }
        };

        // Мосты ViewModel → представление (этап 2 MVVM, дополнение):
        ViewModel.RecordingChanged += (s, e) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateRecordButtons();
                if (!string.IsNullOrEmpty(ViewModel.RecordError))
                {
                    ShowStreamError(ViewModel.RecordError);
                    ViewModel.RecordError = null;
                }
            });
        ViewModel.FilterChanged += (s, e) =>
            DispatcherQueue.TryEnqueue(RefreshOverlayChannelGroups);
        ViewModel.ReminderToastRequested += (s, e) =>
            ShowReminderToast(e);
        ViewModel.SettingsSaveRequested += (s, e) =>
        {
            _settingsSaveDebounceTimer.Stop();
            _settingsSaveDebounceTimer.Start();
        };
        ViewModel.ScrollToProgramRequested += (s, e) =>
            DispatcherQueue.TryEnqueue(async () => await ScrollToCurrentProgramAsync());
        ViewModel.ArchivePlayErrorRequested += (s, e) =>
            DispatcherQueue.TryEnqueue(() => ShowStreamError(e));
        ViewModel.SleepTimerExpired += (s, e) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                // Действие настраивается в настройках (SleepTimerAction):
                // остановить воспроизведение (по умолчанию), закрыть
                // приложение или выключить компьютер. Для Exit/Shutdown
                // закрываем окно — его Closed-обработчик сам остановит
                // плеер и запись и сохранит настройки перед Environment.Exit.
                switch (ViewModel.AppSettings.SleepTimerAction)
                {
                    case "Exit":
                        _logger.LogInformation("Таймер сна: закрываю приложение.");
                        MainWindow.Instance?.Close();
                        break;
                    case "Shutdown":
                        _logger.LogInformation("Таймер сна: выключаю компьютер.");
                        if (!TryShutdownPc())
                        {
                            // shutdown.exe не запустился — хотя бы закрываем
                            // приложение, как в режиме Exit.
                            _logger.LogWarning("Таймер сна: shutdown.exe не запустился, закрываю только приложение.");
                        }
                        MainWindow.Instance?.Close();
                        break;
                    default:
                        StopPlayback();
                        _logger.LogInformation("Воспроизведение остановлено по таймеру сна.");
                        break;
                }
            });
        ViewModel.SleepTimerChanged += (s, e) =>
            DispatcherQueue.TryEnqueue(UpdateSleepTimerDisplays);

        InitializeComponent();
        // Пока пользователь ни разу не кликнул по окну, ни один элемент не
        // имеет фокуса — туннелирующий PreviewKeyDown страницы в этом
        // состоянии может не приходить вовсе, и горячие клавиши «не работали
        // до первого клика». Делаем страницу фокусируемой и задаём фокус на
        // старте: все клавиши достаются странице, пока пользователь не кликнет
        // по конкретному элементу.
        IsTabStop = true;
        Loaded += (s, e) => Focus(FocusState.Programmatic);
        Loaded += async (s, e) =>
        {
            try
            {
                await InitializeAsync();
            }
            catch (Exception ex)
            {
                // Раньше исключение отсюда долетало только до
                // App.OnUnhandledException без указания, что упало именно
                // при старте страницы — из-за этого EPG/каналы могли просто
                // не появиться без единой зацепки, откуда искать причину.
                _logger.LogError(ex, "InitializeAsync: исключение при старте страницы.");
            }
        };
        Unloaded += (s, e) =>
        {
            _overlayHideTimer.Stop();
            _currentProgramRefreshTimer.Stop();
            _archivePositionTimer.Stop();
            _archiveSeekDebounceTimer.Stop();
            _reminderTimer.Stop();
            _settingsSaveDebounceTimer.Stop();
            _channelNumberInputTimer.Stop();
            StopPlayback();
        };

        _currentProgramRefreshTimer.Tick += (s, e) =>
        {
            // Fire-and-forget: метод сам уступает поток UI пачками (Yield)
            // и пропускает тик, если идёт полная загрузка EPG.
            _ = ViewModel.EpgViewModel.RefreshCurrentProgramsLightAsync();
        };
        _currentProgramRefreshTimer.Start();

        // Ход полосы перемотки архива (обе панели). Обновление свойств VM —
        // на UI-потоке (DispatcherTimer), чтобы x:Bind и слайдеры не гонялись
        // с фоновым потоком. Тем же тиком обновляется оверлей статистики
        // (Ctrl+J) — раз в секунду достаточно и для него.
        _archivePositionTimer.Tick += (s, e) =>
        {
            Player.RefreshArchivePosition();
            UpdateArchiveSeekBar();
            UpdateStatsOverlay();
            ViewModel.CheckSleepTimer();
            UpdateSleepTimerDisplays();
        };
        _archivePositionTimer.Start();

        // Коммит перемотки по дебаунсу (см. поле _archiveSeekDebounceTimer).
        _archiveSeekDebounceTimer.Tick += (s, e) => CommitArchiveSeek();

        // Горячие клавиши (см. регион «Горячие клавиши» ниже). Подписка НЕ на
        // страницу, а на КОРНЕВОЙ элемент XamlRoot: окно хостит страницу внутри
        // Grid+Frame, туннелирующий PreviewKeyDown идёт от корня к
        // сфокусированному элементу — пока фокуса внутри страницы нет, клавиши
        // до страницы не доходили вовсе (Ctrl+J «не работал» до клика по
        // некоторым элементам). Корень ловит и «фокус ни на чём», и любой
        // фокус внутри страницы. XamlRoot доступен после Loaded.
        Loaded += (s, e) =>
        {
            if (_hotkeysAttached || XamlRoot?.Content is not UIElement root)
            {
                return;
            }
            _hotkeysAttached = true;
            root.PreviewKeyDown += OnPagePreviewKeyDown;
        };

        // Ввод номера канала цифрами: коммит по таймауту 3 с (Enter — сразу).
        _channelNumberInputTimer.Tick += (s, e) => CommitChannelNumber();

        // Исходное состояние кнопок беззвучного режима (иконки).
        UpdateMuteButtons();

        // Проверка напоминаний о передачах: ищем передачи, до начала которых
        // осталось <= ReminderMinutes, и показываем тосты Windows.
        _reminderTimer.Tick += (s, e) =>
        {
            _ = ViewModel.CheckRemindersAsync();
            ViewModel.CheckScheduledRecordings();
        };
        _reminderTimer.Start();

        // Дебаунс записи настроек (избранное, последний канал, напоминания).
        _settingsSaveDebounceTimer.Tick += (s, e) =>
        {
            _settingsSaveDebounceTimer.Stop();
            _ = ViewModel.SaveSettingsAsync();
        };

        // Общий таймер автоскрытия для обоих оверлеев: полноэкранного
        // (список каналов + управление) и оконного (WindowedVideoOverlay
        // поверх видео). Какой именно прятать — решают сами методы скрытия.
        _overlayHideTimer.Tick += (s, e) =>
        {
            _overlayHideTimer.Stop();
            HideFullScreenOverlay();
            HideWindowedVideoOverlay();
        };

        // Дебаунс сохранения громкости: слайдер при перетаскивании меняет
        // значение десятки раз в секунду — писать settings.json на каждый
        // тик нельзя. Пишем через 700 мс после последнего движения.
        _volumeSaveDebounceTimer.Tick += (s, e) =>
        {
            _volumeSaveDebounceTimer.Stop();
            _ = SaveVolumeToSettingsAsync();
        };

        // Раньше нажатие крестика закрывало окно, но процесс жил ещё
        // несколько секунд — медиа-конвейер MediaPlayer'а с живым потоком
        // не даёт WinUI-процессу завершиться сразу. Останавливаем плеер
        // и выходим немедленно. (Громкость к этому моменту уже сохранена
        // дебаунсом; блокироваться на WinRT-async здесь нельзя — дедлок.)
        MainWindow.Instance!.Closed += (_, _) =>
        {
            try
            {
                _overlayHideTimer.Stop();
                _volumeSaveDebounceTimer.Stop();
                _settingsSaveDebounceTimer.Stop();
                _archivePositionTimer.Stop();
                _archiveSeekDebounceTimer.Stop();

                // Позиция/размер окна и ширина панели каналов — вместе с
                // остальными настройками. SettingsService делает синхронный
                // файловый ввод-вывод, поэтому дожидаемся записи до Exit —
                // fire-and-forget мог не успеть завершиться.
                var placement = MainWindow.Instance.CapturePlacement();
                if (placement != null)
                {
                    ViewModel.AppSettings.WindowPlacement = placement;
                }
                ViewModel.AppSettings.ChannelListWidth = _isFullScreen
                    ? _channelListExpandedWidth
                    : Math.Max(0, ChannelListColumn.ActualWidth);
                ViewModel.AppSettings.Volume = Player.LastUserVolume ?? 1.0;
                ViewModel.SaveSettingsAsync().GetAwaiter().GetResult();

                // Идущая запись останавливается — файл остаётся валидным TS
                // (Kill процесса = обрыв потока, MPEG-TS переживает это).
                ViewModel.Recording.Stop();

                if (Player.Player != null)
                {
                    Player.Player.Source = null;
                    Player.Player.Dispose();
                }
            }
            catch
            {
                // Процесс всё равно завершится ниже — уборка best-effort.
            }

            // Выгружаем буферы Serilog до немедленного выхода: обработчик
            // Closed в App подписан ПОСЛЕ этого и до Environment.Exit(0)
            // уже не выполнится.
            Serilog.Log.CloseAndFlush();

            Environment.Exit(0);
        };
    }

    private async Task InitializeAsync()
    {
        // _epgService и ViewModel.EpgViewModel уже созданы в конструкторе
        // (см. комментарий у поля _epgService) — здесь их больше не пересоздаём
        // и не переподставляем, только наполняем данными.

        // ВАЖНО: _channelRepository изначально пуст (см. ChannelRepository —
        // "никаких демо-каналов по умолчанию"). Раньше здесь сразу вызывался
        // epgService.GetChannelsAsync(), который кэширует результат
        // channelRepository.GetAllChannelsAsync() — то есть кэшировал ПУСТОЙ
        // список, ещё до того как ниже добавлялись каналы из плейлиста.
        // Каналы из плейлиста при этом добавлялись только в ViewModel.Channels,
        // а не в channelRepository — репозиторий, из которого EPGService берёт
        // каналы, так и оставался пустым. Из-за этого EpgViewModel.LoadEPGAsync()
        // (который тоже вызывает epgService.GetChannelsAsync() и получал тот же
        // закэшированный пустой список) затирал уже показанные каналы, и EPG
        // пропадал — в том числе при каждом "Обновить EPG" (RefreshEPGAsync
        // чистит кэш EPGService, но channelRepository остаётся пустым, и кэш
        // тут же переполняется тем же пустым списком заново).
        //
        // Фикс: сначала собираем полный список каналов (плейлист) и кладём
        // его в channelRepository, и только потом первый раз обращаемся к
        // epgService.GetChannelsAsync() — тогда он закэширует правильный
        // список, а не пустой.
        var savedSettings = await _settingsService.LoadAsync();
        ViewModel.AppSettings = savedSettings;
        var initialChannels = new List<ChannelViewModel>();

        // Язык и тема — до построения любого UI-текста.
        L.SetLanguage(savedSettings.Language);
        ApplyTheme(savedSettings.Theme);
        ApplyLanguage();

        // Ширина панели каналов, выбранная перетаскиванием разделителя.
        if (savedSettings.ChannelListWidth >= 240 && savedSettings.ChannelListWidth <= 640)
        {
            ChannelListColumn.Width = new GridLength(savedSettings.ChannelListWidth);
            _channelListExpandedWidth = savedSettings.ChannelListWidth;
        }

        // Восстанавливаем сохранённую громкость: применяется к первому и
        // всем последующим плеерам через Player.LastUserVolume, а оба слайдера
        // (оконный и полноэкранный оверлеи) сразу показывают её.
        {
            var saved = Math.Clamp(savedSettings.Volume, 0.0, 1.0);
            Player.LastUserVolume = saved;
            _isVolumeSliderSyncing = true;
            VideoOverlayVolumeSlider.Value = saved;
            OverlayVolumeSlider.Value = saved;
            _isVolumeSliderSyncing = false;
        }

        // Режим отображения видео (вписать/растянуть/обрезать).
        ApplyVideoStretch();

        // Оверлей статистики — если был включён в прошлой сессии.
        if (savedSettings.StatsOverlayVisible)
        {
            SetStatsOverlayVisible(show: true, persist: false);
        }

        // Миграция с одной версии настроек: единственный PlaylistUrl прошлых
        // версий становится первым плейлистом списка (Id=1 — под него же
        // мигрируется и старый файл кэша плейлиста в PlaylistCacheService).
        if (ViewModel.AppSettings.Playlists.Count == 0 &&
            !string.IsNullOrWhiteSpace(savedSettings.PlaylistUrl))
        {
            ViewModel.AppSettings.Playlists.Add(new PlaylistSource
            {
                Id = 1,
                Name = DefaultPlaylistName(savedSettings.PlaylistUrl),
                Url = savedSettings.PlaylistUrl,
                LastWatchedChannel = savedSettings.LastWatchedChannel,
                // Источники EPG остаются те же (обычно единственный фид epg.one) —
                // теперь как личный набор этого плейлиста.
                EpgSources = savedSettings.EpgSources
                    .Select(s => new EPGSource { Url = s.Url, IsEnabled = s.IsEnabled })
                    .ToList()
            });
            ViewModel.AppSettings.ActivePlaylistId = 1;
            await _settingsService.SaveAsync(ViewModel.AppSettings);
        }

        // Активный плейлист: каналы в список грузятся только из него.
        _activePlaylist = ViewModel.AppSettings.Playlists
            .FirstOrDefault(p => p.Id == ViewModel.AppSettings.ActivePlaylistId)
            ?? ViewModel.AppSettings.Playlists.FirstOrDefault();
        if (_activePlaylist != null)
        {
            ViewModel.AppSettings.ActivePlaylistId = _activePlaylist.Id;
            initialChannels.AddRange(await LoadPlaylistChannelsAsync(_activePlaylist));
        }

        // Id назначаются один раз для обоих путей появления каналов (скачанный
        // плейлист или кэш) — до этого ChannelViewModel.Id может быть default.
        var channelId = 1;
        foreach (var channel in initialChannels)
        {
            channel.Id = channelId++;
        }

        foreach (var channel in initialChannels)
        {
            // Наполняем именно _channelRepository — это тот объект, на который
            // ссылается epgService, и по которому EPGService.GetEPGEntriesAsync
            // ищет TvgId канала при сопоставлении с XMLTV-программами.
            await _channelRepository.AddChannelAsync(channel);
        }

        var channels = await _epgService.GetChannelsAsync();
        ViewModel.Channels = new ObservableCollection<ChannelViewModel>(channels);

        // Избранное из настроек (по имени канала — см. комментарий в AppSettings).
        if (ViewModel.AppSettings.FavoriteChannels.Count > 0)
        {
            var favorites = new HashSet<string>(ViewModel.AppSettings.FavoriteChannels, StringComparer.OrdinalIgnoreCase);
            foreach (var channel in ViewModel.Channels)
            {
                channel.IsFavorite = favorites.Contains(channel.Name);
            }
        }

        ViewModel.EpgViewModel.SetChannels(ViewModel.Channels.ToList());
        ViewModel.UpdateChannelCountText();

        // RefreshGroups пересобирает Groups и назначает SelectedGroup, а
        // FilterChannels пересчитывает DisplayedChannels (представление
        // обновит GroupFilterComboBox и оверлей через событие FilterChanged).
        ViewModel.RefreshGroups();
        ViewModel.FilterChannels();
        UpdatePlaylistMenu();

        // Выбираем первый канал ДО запуска загрузки EPG: SelectedChannel
        // нужен для x:Bind EPG-панели, и раньше он назначался только после
        // полной загрузки EPG (при первом скачивании 45 МБ фида это десятки
        // секунд) — всё это время панель программ оставалась пустой.
        // Если в настройках есть последний смотренный канал — выбираем его
        // и автопродолжаем воспроизведение (fire-and-forget: старт и так
        // происходит в фоне, EPG грузится дальше независимо).
        if (ViewModel.Channels.Count > 0)
        {
            // Последний смотренный канал хранится на каждый плейлист свой
            // (PlaylistSource.LastWatchedChannel); глобальный — запасной
            // вариант на случай отсутствия (свежая миграция со старых настроек).
            var lastWatchedName = _activePlaylist?.LastWatchedChannel
                                  ?? ViewModel.AppSettings.LastWatchedChannel;
            var lastWatched = string.IsNullOrWhiteSpace(lastWatchedName)
                ? null
                : ViewModel.Channels.FirstOrDefault(c =>
                    string.Equals(c.Name, lastWatchedName, StringComparison.OrdinalIgnoreCase));

            // Группу последнего канала — в фильтр списка ДО выбора самого
            // канала: смена SelectedGroup прогоняет FilterChannels, который
            // на время перестраивает DisplayedChannels и сбрасывает выделение
            // (TwoWay SelectedItem) — выбор канала после фильтра ничего не
            // теряет. Каноническая строка из Groups — чтобы SelectedItem
            // совпал с пунктом комбобокса. Старт возвращает контекст прошлой
            // сессии: восстановленный канал виден в своём подразделе.
            var lastGroup = lastWatched?.Group?.Trim();
            if (!string.IsNullOrEmpty(lastGroup))
            {
                var canonicalGroup = ViewModel.Groups.FirstOrDefault(
                    g => string.Equals(g, lastGroup, StringComparison.OrdinalIgnoreCase));
                if (canonicalGroup != null)
                {
                    ViewModel.SelectedGroup = canonicalGroup;
                }
            }

            ViewModel.SelectedChannel = lastWatched ?? ViewModel.Channels[0];

            // Список каналов — прокрутить к восстановленному каналу: без этого
            // он может оказаться за пределами первого экрана (список длинный,
            // SelectedItem подсвечивает строку, но не показывает её).
            _ = ScrollSelectedChannelIntoViewAsync();

            if (lastWatched != null)
            {
                _ = ContinueWatchingAsync(lastWatched);
            }
        }

        // Даем UI отрисовать список каналов до старта загрузки EPG. Без
        // этого ListView мог получить коллекцию, но не успеть отрисоваться
        // до первого await внутри LoadEPGAsync — и на экране список
        // появлялся только вместе с EPG.
        await Task.Yield();

        // Загрузка EPG больше не блокирует UI (тяжёлая работа в пуле
        // потоков — см. CacheService и EPGService.MergeSources), а
        // программы догружаются в панели по мере готовности.
        await ViewModel.EpgViewModel.LoadEPGAsync();

        // После (пере)загрузки EPG коллекции передач пересобраны — возвращаем
        // колокольчики активных напоминаний.
        ViewModel.ApplyReminderFlags();
    }

    /// <summary>
    /// Имя плейлиста по умолчанию — хост URL (без www), чтобы список плейлистов
    /// был узнаваемым без обязательного ввода имени при добавлении.
    /// </summary>
    internal static string DefaultPlaylistName(string url)
    {
        // Локальный файл плейлиста — имя по файлу без расширения.
        if (System.IO.File.Exists(url))
        {
            return System.IO.Path.GetFileNameWithoutExtension(url);
        }

        try
        {
            var host = new Uri(url).Host;
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? host[4..]
                : host;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Не удалось извлечь хост из URL плейлиста — показываем исходный URL.");
            return url;
        }
    }

    /// <summary>
    /// Загружает каналы плейлиста при старте и при переключении: если кэш
    /// этого плейлиста свеж (PlaylistRefreshDays не истёк и формат актуален) —
    /// каналы берутся из кэша без скачивания; иначе M3U перекачивается и кэш
    /// обновляется. При сбое скачивания отдаётся пусть и просроченный кэш —
    /// переключение/запуск не должно оставлять пользователя без каналов.
    /// </summary>
    private async Task<List<ChannelViewModel>> LoadPlaylistChannelsAsync(PlaylistSource playlist)
    {
        var result = new List<ChannelViewModel>();
        var playlistCache = await _playlistCacheService.LoadAsync(playlist.Id);
        var refreshDue = playlistCache == null ||
                         playlistCache.Channels.Count == 0 ||
                         playlistCache.FormatVersion < PlaylistCache.CurrentFormatVersion ||
                         IsCacheDue(playlistCache.SavedAtUtc, ViewModel.AppSettings.PlaylistRefreshDays);

        if (!refreshDue && playlistCache != null)
        {
            foreach (var cached in playlistCache.Channels)
            {
                result.Add(CachedToChannel(cached));
            }

            _logger.LogInformation(
                "Плейлист {Playlist} взят из локального кэша (возраст {Age:F1} ч) — скачивание пропущено.",
                playlist.Name, (DateTime.UtcNow - playlistCache.SavedAtUtc).TotalHours);
            return result;
        }

        try
        {
            // Локальный файл (m3u на диске) — без сети; URL — скачивание.
            var playlistChannels = System.IO.File.Exists(playlist.Url)
                ? await _m3uParserService.ParseFromFileAsync(playlist.Url)
                : await _m3uParserService.ParseFromUrlAsync(playlist.Url);
            result.AddRange(playlistChannels);
            await SavePlaylistCacheAsync(playlist.Id, playlistChannels);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось загрузить плейлист {Playlist} ({Url}).", playlist.Name, playlist.Url);

            if (playlistCache != null)
            {
                foreach (var cached in playlistCache.Channels)
                {
                    result.Add(CachedToChannel(cached));
                }
            }
        }

        return result;
    }

    private static ChannelViewModel CachedToChannel(Models.CachedChannel cached) => new()
    {
        Name = cached.Name,
        StreamUrl = cached.StreamUrl,
        LogoUrl = cached.LogoUrl,
        Group = cached.Group,
        TvgId = cached.TvgId,
        CatchupDays = cached.CatchupDays
    };

    /// <summary>
    /// Наполняет подменю «Сменить плейлист» в меню настроек: активный отмечен
    /// галочкой (ToggleMenuFlyoutItem в стиле остальных пунктов), клик по
    /// пункту переключает плейлист. Подменю прячется, когда плейлист один
    /// (переключать нечего). Вызывается при старте и после изменения списка
    /// плейлистов в диалоге настроек.
    /// </summary>
    private void UpdatePlaylistMenu()
    {
        var playlists = ViewModel.AppSettings.Playlists;
        SwitchPlaylistSubMenu.Items.Clear();
        foreach (var playlist in playlists)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = playlist.Name,
                IsChecked = playlist.Id == ViewModel.AppSettings.ActivePlaylistId,
                Tag = playlist
            };
            item.Click += SwitchPlaylistMenuItem_Click;
            SwitchPlaylistSubMenu.Items.Add(item);
        }

        SwitchPlaylistSubMenu.Visibility = playlists.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SwitchPlaylistMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: PlaylistSource playlist } &&
            playlist.Id != ViewModel.AppSettings.ActivePlaylistId)
        {
            await SwitchPlaylistAsync(playlist);
        }
    }

    /// <summary>
    /// Переключение активного плейлиста: останавливает воспроизведение, чистит
    /// каналы предыдущего плейлиста (репозиторий + список + EPG) и наполняет
    /// их каналами нового — той же логикой кэша/обновления, что и при старте.
    /// Автопродолжение последнего канала не запускается: переключение —
    /// осознанное действие, видео включится кликом по каналу.
    /// </summary>
    private async Task SwitchPlaylistAsync(PlaylistSource playlist)
    {
        if (_activePlaylist?.Id == playlist.Id)
        {
            return;
        }

        try
        {
            ViewModel.Player.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Остановка плеера при переключении плейлиста.");
        }

        _activePlaylist = playlist;
        ViewModel.AppSettings.ActivePlaylistId = playlist.Id;
        await _settingsService.SaveAsync(ViewModel.AppSettings);

        var channels = await LoadPlaylistChannelsAsync(playlist);

        var channelId = 1;
        foreach (var channel in channels)
        {
            channel.Id = channelId++;
        }

        await _channelRepository.Clear();
        foreach (var channel in channels)
        {
            await _channelRepository.AddChannelAsync(channel);
        }

        ViewModel.Channels = new ObservableCollection<ChannelViewModel>(channels);

        // Избранное глобальное (по имени канала) — переживает переключение.
        if (ViewModel.AppSettings.FavoriteChannels.Count > 0)
        {
            var favorites = new HashSet<string>(ViewModel.AppSettings.FavoriteChannels, StringComparer.OrdinalIgnoreCase);
            foreach (var channel in ViewModel.Channels)
            {
                channel.IsFavorite = favorites.Contains(channel.Name);
            }
        }

        ViewModel.EpgViewModel.SetChannels(ViewModel.Channels.ToList());
        ViewModel.UpdateChannelCountText();
        ViewModel.RefreshGroups();
        ViewModel.FilterChannels();

        var lastWatched = string.IsNullOrWhiteSpace(playlist.LastWatchedChannel)
            ? null
            : ViewModel.Channels.FirstOrDefault(c =>
                string.Equals(c.Name, playlist.LastWatchedChannel, StringComparison.OrdinalIgnoreCase));
        ViewModel.SelectedChannel = lastWatched ?? ViewModel.Channels.FirstOrDefault();

        UpdatePlaylistMenu();

        // EPG у каждого плейлиста свой (источники XMLTV в PlaylistSource):
        // после смены набора каналов программы перечитываются с источников
        // нового плейлиста фоном, без очистки дискового кэша общего фида.
        _ = LoadEpgAfterPlaylistSwitchAsync();
    }

    private async Task LoadEpgAfterPlaylistSwitchAsync()
    {
        try
        {
            await ViewModel.EpgViewModel.ReloadForPlaylistAsync();
            ViewModel.ApplyReminderFlags();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Перезагрузка EPG после переключения плейлиста.");
        }
    }

    /// <summary>
    /// Автопродолжение последнего канала при запуске: то же, что клик по
    /// каналу, но без блокировки InitializeAsync и без обновления
    /// LastWatchedChannel (он и есть этот канал).
    /// </summary>
    private async Task ContinueWatchingAsync(ChannelViewModel channel)
    {
        try
        {
            await PlayLiveAsync(channel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Автопродолжение последнего канала ({Name}).", channel.Name);
        }
    }

    /// <summary>
    /// Прокручивает список каналов к восстановленному при старте каналу.
    /// Задержка после Yield — как у ScrollToCurrentProgramAsync: ListView
    /// должен успеть отрисовать элементы после фильтрации по группе.
    /// </summary>
    private async Task ScrollSelectedChannelIntoViewAsync()
    {
        if (ViewModel.SelectedChannel == null)
        {
            return;
        }

        await Task.Yield();
        await Task.Delay(100);
        ChannelsListView.ScrollIntoView(ViewModel.SelectedChannel);
    }

    /// <summary>
    /// Применяет тему к корневому элементу окна: Light/Dark/Default (системная).
    /// RequestedTheme перекрашивает все ThemeResource-ки — на лету, без
    /// перезапуска. Вызывается на старте и сразу при смене в настройках.
    /// </summary>
    private void ApplyTheme(string theme)
    {
        var elementTheme = theme switch
        {
            "Dark" => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _ => ElementTheme.Default
        };

        if (MainWindow.Instance?.Content is FrameworkElement root)
        {
            root.RequestedTheme = elementTheme;
        }
    }

    /// <summary>
    /// Переводит статичные элементы страницы (они имеют x:Name) на текущий
    /// язык (Services/L). Строки, собираемые в коде (диалоги, сообщения),
    /// переводятся в момент построения — диалог настроек пересобирается при
    /// каждом открытии, поэтому подхватывает язык сразу.
    /// </summary>
    private void ApplyLanguage()
    {
        ChannelsHeaderText.Text = L.T("Каналы", "Channels");
        OverlayChannelsHeaderText.Text = L.T("Каналы", "Channels");
        ChannelSearchBox.PlaceholderText = L.T("Поиск...", "Search...");

        EpgHeaderText.Text = L.T("— Программа передач", "— TV Guide");
        EpgHeaderHintText.Text = L.T("клик по передаче — смотреть с начала", "click a programme to watch from the start");
        EmptyChannelEpgText.Text = L.T("Нет данных о программах для этого канала", "No programme data for this channel");
        EmptyEpgTitle.Text = L.T("EPG данные недоступны", "EPG data unavailable");
        EmptyEpgHint.Text = L.T("Выберите источник EPG или обновите данные", "Choose an EPG source or refresh the data");
        EmptyEpgRefreshButton.Content = L.T("Обновить EPG", "Refresh EPG");

        // Кнопки «В эфир» и подсказки панелей управления.
        VideoOverlayBackToLiveButton.Content = L.T("В эфир", "Live");
        OverlayBackToLiveButton.Content = L.T("В эфир", "Live");
        ToolTipService.SetToolTip(VideoOverlaySettingsButton, L.T("Настройки", "Settings"));
        ToolTipService.SetToolTip(VideoOverlayFullScreenButton, L.T("Развернуть плеер на весь экран (F или двойной клик)", "Full screen (F or double-click)"));
        ToolTipService.SetToolTip(OverlayEpgButton, L.T("Показать/скрыть EPG", "Show/hide TV guide"));
        ToolTipService.SetToolTip(ExitFullScreenButton, L.T("Выйти из полноэкранного режима (Esc)", "Exit full screen (Esc)"));
        ToolTipService.SetToolTip(OverlayPauseButton, L.T("Пауза (архив, пробел)", "Pause (archive, Space)"));

        ViewModel.UpdateChannelCountText();
        ApplyEpgVisibility();
        UpdateArchivePauseButton();
        UpdateRecordButtons();
        UpdateMuteButtons();
        UpdateStretchButtons();
        UpdateSleepTimerDisplays();
    }

    // UpdateChannelCountText() удалён: ChannelCountText.Text забинден
    // OneWay на ViewModel.ChannelCountText (MainPage.xaml, строка 179).
    // Язык переключается через ViewModel.UpdateChannelCountText().

    // RefreshGroupFilterOptions() удалён: GroupFilterComboBox.ItemsSource и
    // SelectedItem теперь забиндены на ViewModel.Groups и
    // ViewModel.SelectedGroup. Обновление — через ViewModel.RefreshGroups().

    // ApplyChannelFilters() удалён: фильтрация теперь полностью в
    // ViewModel.FilterChannels(), которая автоматически вызывается
    // при изменении SearchQuery / SelectedGroup через TwoWay-биндинги.
    // RefreshOverlayChannelGroups() вызывается через событие FilterChanged.

    /// <summary>
    /// Небольшая обёртка над списком каналов одной группы для группированного
    /// ListView в полноэкранном оверлее — GroupStyle.HeaderTemplate биндится
    /// на её свойство Key.
    /// </summary>
    private sealed class ChannelGroup : List<ChannelViewModel>
    {
        public string Key { get; }

        public ChannelGroup(string key, IEnumerable<ChannelViewModel> items) : base(items)
        {
            Key = key;
        }
    }

    /// <summary>
    /// Перестраивает сгруппированный источник для OverlayChannelsListView на
    /// основе текущего ViewModel.DisplayedChannels. Порядок каналов — ТОТ ЖЕ,
    /// что в оконном списке: избранные блоком наверх, дальше порядок плейлиста
    /// (OrderByDescending(IsFavorite) в FilterChannels — стабильная сортировка).
    /// Группы идут по первому вхождению своих каналов, внутри группы — тоже
    /// порядок плейлиста; иначе список в fullscreen не совпадал с оконным, и
    /// номер из цифрового ввода указывал не на тот канал. Избранные выводятся
    /// в группу «★ Избранное» наверху и исключаются из тематических групп —
    /// чтобы один канал не встречался в списке дважды (иначе путается выделение).
    /// </summary>
    private void RefreshOverlayChannelGroups()
    {
        var favorites = new List<ChannelViewModel>();

        // Dictionary держит ключи без учёта регистра (дубликаты групп
        // «Фильмы»/«фильмы» схлопываются), orderedGroups — порядок вставки.
        var buckets = new Dictionary<string, ChannelGroup>(StringComparer.OrdinalIgnoreCase);
        var orderedGroups = new List<ChannelGroup>();

        foreach (var channel in ViewModel.DisplayedChannels)
        {
            if (channel.IsFavorite)
            {
                favorites.Add(channel);
                continue;
            }

            var key = string.IsNullOrWhiteSpace(channel.Group) ? "Без группы" : channel.Group!.Trim();
            if (!buckets.TryGetValue(key, out var group))
            {
                group = new ChannelGroup(key, Enumerable.Empty<ChannelViewModel>());
                buckets[key] = group;
                orderedGroups.Add(group);
            }
            group.Add(channel);
        }

        if (favorites.Count > 0)
        {
            orderedGroups.Insert(0, new ChannelGroup(FavoritesOption, favorites));
        }

        var cvs = new CollectionViewSource
        {
            IsSourceGrouped = true,
            Source = orderedGroups
        };

        OverlayChannelsListView.ItemsSource = cvs.View;
    }

    // ChannelSearchBox.Text и GroupFilterComboBox.SelectedItem теперь забиндены
    // TwoWay на ViewModel.SearchQuery / ViewModel.SelectedGroup — фильтрация
    // автоматически срабатывает через OnSearchQueryChanged / OnSelectedGroupChanged.
    // Убраны бывшие здесь ChannelSearchBox_TextChanged и
    // GroupFilterComboBox_SelectionChanged, которые дублировали
    // ViewModel.FilterChannels().

    private void ChannelsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ChannelViewModel channel)
        {
            ViewModel.SelectAndPlayChannelCommand.Execute(channel);
        }
    }

    private async void AddChannelButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Добавить канал",
            PrimaryButtonText = "Добавить",
            CloseButtonText = "Отмена",
            XamlRoot = ((Button)sender).XamlRoot
        };

        var panel = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Vertical, Spacing = 12, Width = 300 };
        var nameBox = new TextBox { Header = "Название", PlaceholderText = "Введите название канала" };
        var urlBox = new TextBox { Header = "URL потока", PlaceholderText = "Введите URL потока (m3u8)" };
        panel.Children.Add(nameBox);
        panel.Children.Add(urlBox);
        dialog.Content = panel;
        dialog.PrimaryButtonClick += (s, args) =>
        {
            var name = nameBox.Text.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                var newChannel = new ChannelViewModel
                {
                    Id = ViewModel.Channels.Count + 1,
                    Name = name,
                    IsLive = false,
                    StreamUrl = urlBox.Text.Trim()
                };
                ViewModel.Channels.Add(newChannel);
                ViewModel.UpdateChannelCountText();
                ViewModel.RefreshGroups();
                ViewModel.FilterChannels();
            }
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// Показывает/прячет кнопку паузы архива в полноэкранном оверлее и
    /// переключает её значок (пауза ↔ воспроизведение) по текущему состоянию.
    /// Вызывается из UpdateArchiveBanner при каждой смене состояния плеера и
    /// непосредственно после переключения паузы.
    /// </summary>
    private void UpdateArchivePauseButton()
    {
        var isArchiveActive = Player.IsArchivePlaying && Player.Player != null;
        OverlayPauseButton.Visibility = isArchiveActive ? Visibility.Visible : Visibility.Collapsed;
        VideoOverlayPauseButton.Visibility = isArchiveActive ? Visibility.Visible : Visibility.Collapsed;
        OverlayBackToLiveButton.Visibility = isArchiveActive ? Visibility.Visible : Visibility.Collapsed;
        VideoOverlayBackToLiveButton.Visibility = isArchiveActive ? Visibility.Visible : Visibility.Collapsed;

        var channel = Player.CurrentPlayerChannelId != null
            ? ViewModel.Channels.FirstOrDefault(c => c.Id == Player.CurrentPlayerChannelId.Value)
            : null;
        var isPaused = isArchiveActive && channel is { IsPlaying: false };

        // Полоса перемотки архива живёт в тех же панелях, что и кнопка паузы.
        WindowedArchiveSeekPanel.Visibility = isArchiveActive ? Visibility.Visible : Visibility.Collapsed;
        OverlayArchiveSeekPanel.Visibility = isArchiveActive ? Visibility.Visible : Visibility.Collapsed;
        if (isArchiveActive)
        {
            Player.RefreshArchivePosition();
            UpdateArchiveSeekBar();
        }

        // Нарисованные иконки (AppIcons): «плей» — на паузе, «пауза» — играет.
        OverlayPauseButton.Content = isPaused ? AppIcons.Play(20) : AppIcons.Pause(20);
        ToolTipService.SetToolTip(OverlayPauseButton, isPaused ? L.T("Продолжить (архив, пробел)", "Resume (archive, Space)") : L.T("Пауза (архив, пробел)", "Pause (archive, Space)"));
        VideoOverlayPauseButton.Content = isPaused ? AppIcons.Play(16) : AppIcons.Pause(16);
        ToolTipService.SetToolTip(VideoOverlayPauseButton, isPaused ? L.T("Продолжить (архив, пробел)", "Resume (archive, Space)") : L.T("Пауза (архив, пробел)", "Pause (archive, Space)"));
    }

    // ===================== Делегаты к PlayerViewModel =====================
    // Тяжёлая логика воспроизведения переехала в PlayerViewModel (этап 2
    // MVVM); здесь остались тонкие обёртки, чтобы точки вызова в представлении
    // не менялись. Визуальная реакция (прогресс, ошибки, баннеры, подключение
    // MediaPlayerElement) — через события VM, подписки в конструкторе.

    private Task PlayLiveAsync(ChannelViewModel channel) => Player.PlayLiveAsync(channel);

    private void StopPlayback() => Player.Stop();

    private void EPGProgramsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EPGEntry entry)
        {
            ViewModel.PlayArchiveEntryCommand.Execute(entry);
        }
    }

    /// <summary>
    /// Синхронизирует индикаторы архивного режима с состоянием плеера:
    /// строку в полноэкранном оверлее, кнопку "В эфир" и кнопку паузы в обеих
    /// панелях управления. Постоянный баннер над видео убран — он мешал
    /// просмотру; информация об архиве теперь появляется только вместе с
    /// панелями управления (по движению мыши) и прячется вместе с ними.
    /// Вызывается при каждой смене состояния плеера.
    /// </summary>
    private void UpdateArchiveBanner()
    {
        if (Player.IsArchivePlaying && Player.ArchiveEntry != null)
        {
            var title = $"Архив: {Player.ArchiveEntry.ProgramName} ({Player.ArchiveEntry.StartTime:dd.MM HH:mm})";

            OverlayArchiveText.Text = $"Архив: {Player.ArchiveEntry.ProgramName}";
            OverlayArchiveIndicator.Visibility = Visibility.Visible;

            WindowedArchiveText.Text = $"Архив: {Player.ArchiveEntry.ProgramName}";
            WindowedArchiveIndicator.Visibility = Visibility.Visible;

            // Подсказки кнопок "В эфир" несут ту же информацию, что старый
            // баннер: что именно из архива смотрится.
            ToolTipService.SetToolTip(VideoOverlayBackToLiveButton, title);
            ToolTipService.SetToolTip(OverlayBackToLiveButton, title);
        }
        else
        {
            OverlayArchiveIndicator.Visibility = Visibility.Collapsed;
            WindowedArchiveIndicator.Visibility = Visibility.Collapsed;
        }

        UpdateArchivePauseButton();
    }

    /// <summary>
    /// Показывает или прячет EPG-оверлей поверх видео (визуальная часть:
    /// панель, иконка кнопки, прокрутка к текущей передаче). Истиной о
    /// видимости владеет ViewModel.IsEpgVisible — меняет команда ToggleEpg,
    /// а представление реагирует через EpgVisibilityChanged.
    /// </summary>
    private void ApplyEpgVisibility()
    {
        var visible = ViewModel.IsEpgVisible;
        EpgPanelBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        // Иконка кнопки EPG — всегда календарь (E787), как в полноэкранном
        // оверлее: раньше для скрытого состояния ставился E785 «Unlock»
        // (открытый замок) — читался как «неправильная иконка». Состояние
        // и так видно по самой панели, меняется только подсказка.
        ToolTipService.SetToolTip(VideoOverlayEpgButton, visible ? L.T("Скрыть EPG", "Hide guide") : L.T("Показать EPG", "Show guide"));

        if (visible)
        {
            _ = ScrollToCurrentProgramAsync();
        }
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        SetFullScreenMode(!_isFullScreen);
    }

    private void ExitFullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        SetFullScreenMode(false);
    }

    /// <summary>
    /// Слайдеры громкости в обоих оверлеях (оконном и полноэкранном): меняют
    /// громкость текущего плеера, запоминаются в Player.LastUserVolume (применится
    /// и к следующим плеерам — переключение каналов, архив) и через дебаунс
    /// сохраняются в настройки, чтобы пережить перезапуск приложения.
    /// </summary>
    private void OverlayVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => OnVolumeSliderChanged(e.NewValue);

    private void VideoOverlayVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => OnVolumeSliderChanged(e.NewValue);

    private void OnVolumeSliderChanged(double value)
    {
        if (_isVolumeSliderSyncing)
        {
            return;
        }

        Player.LastUserVolume = value;

        // Движение слайдера снимает беззвучный режим (громкость применяем
        // ниже сами — ClearMute её не трогает намеренно).
        Player.ClearMute();

        if (Player.Player != null)
        {
            Player.Player.Volume = value;
        }

        // Второй слайдер показывает то же значение (флаг отличает
        // программную синхронизацию от действия пользователя).
        SyncVolumeSliders(value);

        // Пишем в настройки не сразу, а после того как пользователь
        // перестал двигать слайдер (см. конструктор).
        _volumeSaveDebounceTimer.Stop();
        _volumeSaveDebounceTimer.Start();
    }

    /// <summary>
    /// Программно выставляет оба слайдера громкости в одно значение,
    /// не провоцируя обратные события ValueChanged.
    /// </summary>
    private void SyncVolumeSliders(double value)
    {
        _isVolumeSliderSyncing = true;
        if (Math.Abs(OverlayVolumeSlider.Value - value) > 0.001)
        {
            OverlayVolumeSlider.Value = value;
        }
        if (Math.Abs(VideoOverlayVolumeSlider.Value - value) > 0.001)
        {
            VideoOverlayVolumeSlider.Value = value;
        }
        _isVolumeSliderSyncing = false;
    }

    private async Task SaveVolumeToSettingsAsync()
    {
        try
        {
            // Пишем в каноническую копию настроек, а не во свежезагруженную —
            // иначе затёрли бы несохранённые избранное/напоминания.
            ViewModel.AppSettings.Volume = Player.LastUserVolume ?? 1.0;
            await _settingsService.SaveAsync(ViewModel.AppSettings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить громкость.");
        }
    }

    // ===================== Избранные каналы =====================

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChannelViewModel channel })
        {
            return;
        }

        ViewModel.ToggleFavoriteCommand.Execute(channel);
    }

    // ===================== Напоминания о передачах =====================

    private void ReminderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: EPGEntry entry })
        {
            return;
        }

        ViewModel.ToggleReminderCommand.Execute(entry);
    }

    /// <summary>
    /// Тост Windows (CommunityToolkit.WinUI.Notifications). В unpackaged-режиме
    /// тосты требуют ярлыка в меню «Пуск» с AUMID — тулкит создаёт его сам при
    /// первом показе; если окружение всё же отказало — логируем однократно
    /// и больше не спамим.
    /// </summary>
    private void ShowReminderToast(Models.ProgramReminder reminder)
    {
        try
        {
            new CommunityToolkit.WinUI.Notifications.ToastContentBuilder()
                .AddText($"Скоро в эфире: {reminder.ProgramName}")
                .AddText($"{reminder.ChannelName} • начало в {reminder.StartTime:HH:mm}")
                .Show();
        }
        catch (Exception ex)
        {
            if (!_toastFailureLogged)
            {
                _toastFailureLogged = true;
                _logger.LogError(ex,
                    "Показ тоста-напоминания не удался (последующие ошибки до перезапуска не логируются).");
            }
        }
    }

    // ===================== Запись передач и каналов =====================

    private void ScheduleRecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: EPGEntry entry })
        {
            return;
        }

        ViewModel.ToggleScheduleRecordCommand.Execute(entry);
    }

    /// <summary>Синхронизирует вид обеих кнопок записи с состоянием сервиса.</summary>
    private void UpdateRecordButtons()
    {
        var active = ViewModel.Recording.IsActive;

        // Нарисованные иконки (AppIcons): идёт запись — красный квадрат STOP,
        // простаивает — красная точка REC. Цвет зашит в фигуру, Foreground
        // кнопки не трогаем.
        VideoOverlayRecordButton.Content = active ? AppIcons.StopSquare(13) : AppIcons.RecordDot(14);
        ToolTipService.SetToolTip(VideoOverlayRecordButton, active
            ? L.T($"Остановить запись ({ViewModel.Recording.OutputPath})", $"Stop recording ({ViewModel.Recording.OutputPath})")
            : L.T("Записать канал", "Record channel"));

        OverlayRecordButton.Content = active ? AppIcons.StopSquare(17) : AppIcons.RecordDot(18);
        ToolTipService.SetToolTip(OverlayRecordButton, active
            ? L.T("Остановить запись", "Stop recording")
            : L.T("Записать канал", "Record channel"));
    }

    // ===================== Беззвучный режим и двойной клик =====================

    private void MuteButton_Click(object sender, RoutedEventArgs e) => Player.ToggleMute();

    /// <summary>
    /// Кнопки M в обеих панелях: иконка (динамик/динамик с крестом), подсказка
    /// и слайдеры (в mute показывают ноль — синхронизация программная и
    /// LastUserVolume не затирает).
    /// </summary>
    private void UpdateMuteButtons()
    {
        VideoOverlayMuteButton.Content = Player.IsMuted ? AppIcons.SpeakerMuted(16) : AppIcons.SpeakerOn(16);
        OverlayMuteButton.Content = Player.IsMuted ? AppIcons.SpeakerMuted(18) : AppIcons.SpeakerOn(18);

        var tooltip = Player.IsMuted
            ? L.T("Включить звук (M)", "Unmute (M)")
            : L.T("Без звука (M)", "Mute (M)");
        ToolTipService.SetToolTip(VideoOverlayMuteButton, tooltip);
        ToolTipService.SetToolTip(OverlayMuteButton, tooltip);

        SyncVolumeSliders(Player.IsMuted ? 0.0 : Player.LastUserVolume ?? Player.Player?.Volume ?? 1.0);
    }

    /// <summary>
    /// Двойной клик по видео — переключение полноэкранного режима (клик по
    /// оверлеям управления и открытому EPG сюда не доходит — они лежат выше
    /// и гасят событие своей областью).
    /// </summary>
    private void VideoArea_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        SetFullScreenMode(!_isFullScreen);
        e.Handled = true;
    }

    /// <summary>
    /// Двойной клик по видимому полноэкранному оверлею. Прозрачный фон
    /// оверлея hit-testable и стоит над видео, поэтому клики по «пустому»
    /// месту не доходили до VideoAreaBorder и не выключали fullscreen —
    /// обрабатываем здесь. По органам управления (кнопки, слайдеры, список
    /// каналов) не срабатываем, только по фону и шапке.
    /// </summary>
    private void FullScreenOverlay_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            AnyAncestorOrSelf(source, element => IsInteractiveControl(element)))
        {
            return;
        }

        SetFullScreenMode(!_isFullScreen);
        e.Handled = true;
    }

    /// <summary>
    /// Элементы, чьи двойные клики принадлежат им самим (кнопки, слайдеры,
    /// списки, поля) — полноэкранный режим они переключать не должны.
    /// </summary>
    private static bool IsInteractiveControl(DependencyObject element) =>
        element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
            or Slider or ListView or ComboBox or TextBox or AutoSuggestBox;

    // ===================== Режимы отображения видео =====================

    // Uniform (вписать, letterbox) → Fill (растянуть без сохранения
    // пропорций) → UniformToFill (обрезать: масштаб с пропорциями, лишнее за
    // краями). Кнопка в обеих панелях управления и клавиша V; выбор
    // сохраняется в настройках (AppSettings.VideoStretch).

    private void StretchButton_Click(object sender, RoutedEventArgs e) => CycleVideoStretch();

    // ===================== Таймер сна =====================

    private async void SleepTimerButton_Click(object sender, RoutedEventArgs e)
    {
        // Диалог выбора времени таймера сна
        var dialog = new ContentDialog
        {
            Title = L.T("Таймер сна", "Sleep Timer"),
            PrimaryButtonText = L.T("Установить", "Set"),
            CloseButtonText = L.T("Отмена", "Cancel"),
            XamlRoot = ((Button)sender).XamlRoot
        };

        var panel = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Vertical, Spacing = 12 };
        var timeOptions = new[] { 15, 30, 45, 60, 90, 120 };
        var radioPanel = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Vertical, Spacing = 4 };

        // Опция "Отключить" если таймер активен
        if (ViewModel.IsSleepTimerActive)
        {
            radioPanel.Children.Add(new RadioButton
            {
                Content = L.T("Отключить таймер", "Turn off timer"),
                Tag = 0,
                GroupName = "SleepTimer"
            });
        }

        foreach (var minutes in timeOptions)
        {
            radioPanel.Children.Add(new RadioButton
            {
                Content = L.T($"{minutes} мин", $"{minutes} min"),
                Tag = minutes,
                GroupName = "SleepTimer"
            });
        }

        // Пользовательский ввод
        var customBox = new TextBox
        {
            Header = L.T("Своё значение (минуты)", "Custom (minutes)"),
            PlaceholderText = "60",
            Width = 200
        };

        // Подсказка о действии по истечении — чтобы «выключить компьютер»
        // из настроек не оказалось сюрпризом.
        var (actionRu, actionEn) = ViewModel.AppSettings.SleepTimerAction switch
        {
            "Exit" => ("закроет программу", "close the app"),
            "Shutdown" => ("выключит компьютер", "shut down the PC"),
            _ => ("остановит воспроизведение", "stop playback")
        };
        var actionHint = new TextBlock
        {
            Text = L.T($"По истечении таймера: {actionRu}.", $"When the timer ends: {actionEn}."),
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        };

        panel.Children.Add(radioPanel);
        panel.Children.Add(customBox);
        panel.Children.Add(actionHint);
        dialog.Content = panel;

        dialog.PrimaryButtonClick += (s, args) =>
        {
            var selectedMinutes = 0;

            // Проверяем RadioButton
            foreach (var child in radioPanel.Children)
            {
                if (child is RadioButton { IsChecked: true, Tag: int tag })
                {
                    selectedMinutes = tag;
                    break;
                }
            }

            // Если не выбран RadioButton, пробуем TextBox
            if (selectedMinutes == 0 && int.TryParse(customBox.Text.Trim(), out var custom) && custom > 0)
            {
                selectedMinutes = custom;
            }

            if (selectedMinutes > 0)
            {
                ViewModel.StartSleepTimer(selectedMinutes);
            }
            else if (ViewModel.IsSleepTimerActive && selectedMinutes == 0)
            {
                ViewModel.StopSleepTimer();
            }
        };

        await dialog.ShowAsync();
        UpdateSleepTimerDisplays();
    }

    private void SleepTimerCancelButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.StopSleepTimer();
        UpdateSleepTimerDisplays();
    }

    /// <summary>
    /// Планирует выключение компьютера (shutdown /s /t 0). shutdown.exe лежит
    /// в System32 и не требует прав администратора; CreateNoWindow, чтобы не
    /// мигало консольное окно. Packaged-режиму запуск стороннего процесса
    /// разрешает объявленная в манифесте capability runFullTrust.
    /// </summary>
    private bool TryShutdownPc()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/s /t 0",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось запустить shutdown.exe для выключения ПК.");
            return false;
        }
    }

    /// <summary>
    /// Обновляет индикаторы таймера сна в обеих панелях (оконной и полноэкранной).
    /// </summary>
    private void UpdateSleepTimerDisplays()
    {
        var isActive = ViewModel.IsSleepTimerActive;
        var remainingText = ViewModel.SleepTimerRemainingText;

        // Оконный индикатор
        WindowedSleepTimerPanel.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        WindowedSleepTimerText.Text = remainingText ?? string.Empty;

        // Полноэкранный индикатор
        OverlaySleepTimerPanel.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        OverlaySleepTimerText.Text = remainingText ?? string.Empty;
    }

    /// <summary>Строковый режим настроек → Stretch медиаэлемента.</summary>
    private static Stretch ParseStretch(string? value) => value switch
    {
        "Fill" => Stretch.Fill,
        "UniformToFill" => Stretch.UniformToFill,
        _ => Stretch.Uniform
    };

    /// <summary>Применяет сохранённый режим отображения (старт приложения).</summary>
    private void ApplyVideoStretch()
    {
        MediaPlayer.Stretch = ParseStretch(ViewModel.AppSettings.VideoStretch);
        UpdateStretchButtons();
    }

    private void CycleVideoStretch()
    {
        var next = MediaPlayer.Stretch switch
        {
            Stretch.Uniform => Stretch.Fill,
            Stretch.Fill => Stretch.UniformToFill,
            _ => Stretch.Uniform
        };

        MediaPlayer.Stretch = next;
        ViewModel.AppSettings.VideoStretch = next.ToString();
        UpdateStretchButtons();

        // Сохранение — тем же дебаунсом, что избранное/последний канал.
        _settingsSaveDebounceTimer.Stop();
        _settingsSaveDebounceTimer.Start();
    }

    private void UpdateStretchButtons()
    {
        var (mode, modeEn) = MediaPlayer.Stretch switch
        {
            Stretch.Fill => ("растянуть", "stretch"),
            Stretch.UniformToFill => ("обрезать", "crop"),
            _ => ("вписать", "fit")
        };
        var tooltip = L.T(
            $"Режим отображения: {mode} (V)",
            $"Video mode: {modeEn} (V)");
        ToolTipService.SetToolTip(VideoOverlayStretchButton, tooltip);
        ToolTipService.SetToolTip(OverlayStretchButton, tooltip);
    }

    // ===================== Оверлей статистики (Ctrl+J) =====================

    private void ShowStreamError(string message)
    {
        StreamErrorText.Text = message;
        StreamErrorText.Visibility = Visibility.Visible;
    }

    // ===================== Перемотка архива =====================

    /// <summary>
    /// Толкает позицию/длительность архива из PlayerViewModel в слайдеры и
    /// подписи обеих панелей (оконной и полноэкранной). Секундный тик и смена
    /// состояния плеера. Пока пользователь тянет ползунок (IsArchiveSeeking),
    /// Value не трогаем — иначе палец сбрасывало бы каждым тиком.
    /// </summary>
    private void UpdateArchiveSeekBar()
    {
        if (!Player.IsArchivePlaying)
        {
            return;
        }

        WindowedArchivePositionText.Text = Player.ArchivePositionText;
        OverlayArchivePositionText.Text = Player.ArchivePositionText;
        WindowedArchiveDurationText.Text = Player.ArchiveDurationText;
        OverlayArchiveDurationText.Text = Player.ArchiveDurationText;

        if (Player.IsArchiveSeeking)
        {
            return;
        }

        var duration = Math.Max(1.0, Player.ArchiveDurationSeconds);
        var position = Math.Clamp(Player.ArchivePositionSeconds, 0.0, duration);

        // Флаг отличает программные присваивания от действий пользователя:
        // ValueChanged на них реагировать не должен.
        _updatingSeekBarValue = true;
        try
        {
            // Maximum раньше Value: Value зажимается в [Minimum..Maximum] в
            // момент присваивания.
            WindowedArchiveSeekBar.Maximum = duration;
            WindowedArchiveSeekBar.Value = position;
            OverlayArchiveSeekBar.Maximum = duration;
            OverlayArchiveSeekBar.Value = position;
        }
        finally
        {
            _updatingSeekBarValue = false;
        }
    }

    private void ArchiveSeekBar_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Пользователь двигает ползунок (drag, тап по дорожке, стрелки
        // клавиатуры): обновляем подпись и взводим отложенный коммит.
        // Программные присваивания из таймера отфильтровываются флагом.
        if (sender is not Slider slider || _updatingSeekBarValue)
        {
            return;
        }

        _activeSeekSlider = slider;
        Player.IsArchiveSeeking = true;

        var text = PlayerViewModel.FormatArchiveTime(slider.Value);
        WindowedArchivePositionText.Text = text;
        OverlayArchivePositionText.Text = text;

        _archiveSeekDebounceTimer.Stop();
        _archiveSeekDebounceTimer.Start();
    }

    private void ArchiveSeekBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            _activeSeekSlider = slider;
        }
        Player.IsArchiveSeeking = true;
    }

    // Оба события (Release и CaptureLost) ведут на один коммит: в WinUI
    // захват указателя при перетаскивании Thumb может не дойти до слайдера,
    // поэтому полагаемся на то, что придёт хотя бы одно из них, а если не
    // придёт ничего — сработает дебаунс по ValueChanged.
    private void ArchiveSeekBar_PointerReleased(object sender, PointerRoutedEventArgs e) => CommitArchiveSeek();

    private void ArchiveSeekBar_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => CommitArchiveSeek();

    /// <summary>Перемотка к текущему значению активного слайдера (идемпотентен).</summary>
    private void CommitArchiveSeek()
    {
        _archiveSeekDebounceTimer.Stop();
        Player.IsArchiveSeeking = false;

        if (_activeSeekSlider == null || !Player.IsArchivePlaying)
        {
            return;
        }

        var target = _activeSeekSlider.Value;
        _activeSeekSlider = null;

        // Fire-and-forget: SeekArchiveAsync перезапускает поток (как
        // переключение канала) и сам обновит состояние через события VM.
        _ = Player.SeekArchiveAsync(target);
    }

    /// <summary>
    /// Проверяет, наступил ли срок обновления кэша по настройкам
    /// (AppSettings.PlaylistRefreshDays / EpgRefreshDays).
    /// savedAtUtc — момент последнего сохранения кэша (UTC).
    /// refreshDays — количество дней из настроек (0 = никогда не обновлять).
    /// </summary>
    private static bool IsCacheDue(DateTime savedAtUtc, int refreshDays)
    {
        if (refreshDays <= 0)
        {
            return false; // "Никогда" — кэш всегда считается свежим
        }

        if (savedAtUtc == default)
        {
            return true; // Нет метки — считаем просроченным
        }

        return (DateTime.UtcNow - savedAtUtc) >= TimeSpan.FromDays(refreshDays);
    }

    /// <summary>
    /// Сохраняет разобранные каналы плейлиста в локальный дисковый кэш
    /// (PlaylistCacheService) — при следующем запуске, если срок обновления
    /// из настроек ещё не наступил, плейлист не придётся перекачивать.
    /// </summary>
    private Task SavePlaylistCacheAsync(int playlistId, List<ChannelViewModel> channels)
    {
        var cache = new Models.PlaylistCache
        {
            FormatVersion = Models.PlaylistCache.CurrentFormatVersion,
            SavedAtUtc = DateTime.UtcNow,
            Channels = channels.Select(c => new Models.CachedChannel
            {
                Name = c.Name,
                StreamUrl = c.StreamUrl,
                LogoUrl = c.LogoUrl,
                Group = c.Group,
                TvgId = c.TvgId,
                CatchupDays = c.CatchupDays
            }).ToList()
        };

        return _playlistCacheService.SaveAsync(playlistId, cache);
    }

    private async void PlaybackSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.PlaybackSettingsDialog(
            ViewModel,
            _settingsService,
            _streamService);
        await dialog.ShowAsync(((MenuFlyoutItem)sender).XamlRoot);
    }

    private async void InterfaceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.InterfaceSettingsDialog(
            ViewModel,
            _settingsService,
            ApplyTheme,
            ApplyLanguage);
        await dialog.ShowAsync(((MenuFlyoutItem)sender).XamlRoot);
    }

    private async void EpgSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.EpgSettingsDialog(
            ViewModel,
            _settingsService);
        await dialog.ShowAsync(((MenuFlyoutItem)sender).XamlRoot);
    }

    private async void PlaylistSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.PlaylistSettingsDialog(
            ViewModel,
            _settingsService,
            _m3uParserService,
            _channelRepository,
            _playlistCacheService,
            App.Services.GetRequiredService<ILogger<Dialogs.PlaylistSettingsDialog>>(),
            SwitchPlaylistAsync);
        await dialog.ShowAsync(((MenuFlyoutItem)sender).XamlRoot);

        // Список/имена плейлистов могли измениться в диалоге — обновляем подменю.
        UpdatePlaylistMenu();
    }

    private async void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        // Тот же текст, что в разделе «О приложении» SettingsDialog.
        var text = new TextBlock
        {
            Text = $"IptvPlayer {Dialogs.SettingsDialog.GetAppVersion()}\n\n" +
                   "IPTV-плеер для плейлистов M3U/M3U8 с программой передач.\n\n" +
                   "Воспроизведение: FFmpeg (демуксинг, декодирование HEVC/AC-3 и др.) поверх Windows App SDK.\n" +
                   "EPG: XMLTV (epg.one), сопоставление каналов — по таблице epg.one/setup-playlist.\n\n" +
                   "Настройки и кэш: %LocalAppData%\\IptvPlayer\n" +
                   $"Лог: {App.LogDirectory}",
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        };
        var dialog = new ContentDialog
        {
            Title = L.T("О программе", "About"),
            Content = text,
            CloseButtonText = L.T("Закрыть", "Close"),
            XamlRoot = ((FrameworkElement)sender).XamlRoot
        };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// Прокручивает список программ передач к текущей (IsCurrent == true)
    /// программе выбранного канала. Вызывается после загрузки EPG для канала.
    /// </summary>
    private async Task ScrollToCurrentProgramAsync()
    {
        if (ViewModel.SelectedChannel == null) return;

        var entries = ViewModel.SelectedChannel.EPGEntries;
        if (entries.Count == 0) return;

        // Дожидаемся, пока ListView отрисует элементы
        await Task.Yield();
        await Task.Delay(50);

        var currentEntry = entries.FirstOrDefault(e => e.IsCurrent);
        if (currentEntry != null)
        {
            EPGProgramsListView.ScrollIntoView(currentEntry);
        }
        else
        {
            // Нет текущей программы — скроллим к ближайшей будущей
            var now = DateTime.Now;
            var nextEntry = entries.FirstOrDefault(e => e.StartTime > now);
            if (nextEntry != null)
            {
                EPGProgramsListView.ScrollIntoView(nextEntry);
            }
            else if (entries.Count > 0)
            {
                EPGProgramsListView.ScrollIntoView(entries[0]);
            }
        }
    }

    private void EPGProgramsListView_Loaded(object sender, RoutedEventArgs e)
    {
        // При первой загрузке списка — скроллим к текущей программе
        _ = ScrollToCurrentProgramAsync();
    }
}
