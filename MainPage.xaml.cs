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
using Serilog;
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
    private static string AllGroupsOption => L.T("Vse_Gruppy");
    private static string FavoritesOption => L.T("Izbrannoe");

    // Все сервисы и ViewModel резолвятся в конструкторе из DI-контейнера
    // App.Services (WinUI не даёт внедрять зависимости в конструкторы
    // XAML-элементов). Раньше каждый сервис создавался здесь же через new
    // (и ещё в шести местах по коду — выходили разные экземпляры
    // SettingsService).
    private readonly IM3UParserService _m3uParserService;
    private readonly IVideoPortalService _videoPortalService;
    private readonly IUpdateService _updateService;
    private readonly ISettingsService _settingsService;
    private readonly IPlaylistCacheService _playlistCacheService;

    /// <summary>
    /// Активный плейлист (AppSettings.Playlists по ActivePlaylistId) — каналы
    /// в списке принадлежат ему; переключение — SwitchPlaylistAsync.
    /// </summary>
    private PlaylistSource? _activePlaylist;

    // Отменяет скачивание предыдущего плейлиста при переключении: без него
    // два GetAsync шли параллельно, и медленный старый мог прийти последним.
    private System.Threading.CancellationTokenSource? _playlistLoadCts;
    private readonly IStreamService _streamService;
    private readonly ChannelRepository _channelRepository;
    private readonly ILogger<MainPage> _logger;

    // Рендер-путь frame server (экспериментальный апскейл, фаза 2).
    private readonly FrameServerRenderer _frameServerRenderer;

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

    // Плеер/запись/состояние EPG живут в ViewModel (этап 2 MVVM); короткий
    // алиас для читаемости в оставшемся коде представления.
    private PlayerViewModel Player => ViewModel.Player;

    // Защита от ложного "пользователь поменял громкость": программная
    // синхронизация слайдера с громкостью плеера при входе в fullscreen
    // тоже вызывает ValueChanged, но не должна трогать Player.LastUserVolume.
    private bool _isVolumeSliderSyncing;

    // Навигация из Hub Page: переданный плейлист и флаг "пришли из хаба"
    private PlaylistSource? _navigatedPlaylist;
    private bool _cameFromHub;
    private bool _skipResume;
    private string? _vodResumeChannelTitle;
    private int _vodResumeEpisodeIndex = -1;

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
        _videoPortalService = services.GetRequiredService<IVideoPortalService>();
        _updateService = services.GetRequiredService<IUpdateService>();
        _settingsService = services.GetRequiredService<ISettingsService>();
        _playlistCacheService = services.GetRequiredService<IPlaylistCacheService>();
        _streamService = services.GetRequiredService<IStreamService>();
        _channelRepository = services.GetRequiredService<ChannelRepository>();
        _epgService = services.GetRequiredService<EPGService>();
        _logger = services.GetRequiredService<ILogger<MainPage>>();
        _frameServerRenderer = new FrameServerRenderer(
            services.GetRequiredService<ILogger<FrameServerRenderer>>());
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
            void ApplyPlayer()
            {
                var player = Player.Player;
                MediaPlayer.SetMediaPlayer(player);

                // Рендер-путь frame server: привязка рендера к новому плееру.
                // Старый плеер Stop() уже мог Dispos'ить — Detach обязателен.
                _frameServerRenderer.Detach();
                if (player != null &&
                    ViewModel.AppSettings.FrameServerRender)
                {
                    var diag = _streamService.CurrentDiagnostics;
                    _frameServerRenderer.Attach(FrameServerPanel, player,
                        diag?.VideoWidth ?? 0, diag?.VideoHeight ?? 0);
                }
            }

            if (DispatcherQueue.HasThreadAccess)
            {
                ApplyPlayer();
            }
            else
            {
                DispatcherQueue.TryEnqueue(ApplyPlayer);
            }
        };
        Player.ArchiveStateChanged += (s, e) =>
            DispatcherQueue.TryEnqueue(UpdateArchiveBanner);
        // Качество VOD портала: кнопки в обеих нижних панелях обновляются при
        // старте/остановке VOD и смене качества.
        Player.VodStateChanged += (s, e) =>
            DispatcherQueue.TryEnqueue(UpdateVodQualityButtons);
        // Выбор серии сериала портала: VM просит — показываем диалог.
        ViewModel.PortalEpisodePickRequested += OnPortalEpisodePickRequested;
        // Возобновление VOD: VM нашла сохранённую позицию — спрашиваем,
        // продолжать ли с места остановки (Primary = продолжить).
        ViewModel.VodResumePromptRequested += OnVodResumePromptRequested;
        ViewModel.RecordingChanged += (s, e) =>
            DispatcherQueue.TryEnqueue(UpdateRecordButtons);
        // Родительский контроль: VM просит PIN при запуске канала
        // заблокированной группы — показываем диалог с выбором длительности.
        ViewModel.ParentalUnlockRequested += channel => ShowParentalPinDialogAsync(channel);
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
                StreamErrorCard.Visibility = string.IsNullOrEmpty(Player.StreamError)
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
                _bufferingStartedAtUtc = null;
                Player.Player.BufferingStarted += (ps, pe) =>
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _bufferingStallCount++;
                        _bufferingStartedAtUtc = DateTime.UtcNow;
                        // Простой буфера в лог: корреляция «фризов» с
                        // истощением read-ahead по моментам времени.
                        Log.Information("Буферизация начата (простой #{Count}).", _bufferingStallCount);
                        UpdateStatsOverlay();
                    });
                Player.Player.BufferingEnded += (ps, pe) =>
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        var duration = _bufferingStartedAtUtc is { } at
                            ? (DateTime.UtcNow - at).TotalSeconds
                            : -1;
                        _bufferingStartedAtUtc = null;
                        Log.Information("Буферизация окончена: длилась {Duration:N1} с.", duration);
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
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // После замены коллекции (FilterChannels) выделение в
                // контролах сбрасывается визуально — OneWay-привязка не
                // перепушит (SelectedChannel не менялся). Возвращаем сами.
                _syncingListSelection = true;
                try
                {
                    ChannelsListView.SelectedItem = ViewModel.SelectedChannel;
                    PosterGridView.SelectedItem = ViewModel.SelectedChannel;
                }
                finally
                {
                    _syncingListSelection = false;
                }

                RefreshOverlayChannelGroups();
            });
            // Пересборка DisplayedChannels (фильтр, поиск, фоновая загрузка
            // EPG) сбрасывает прокрутку списка наверх — возвращаем выбранный
            // канал в видимую область, иначе играющий канал оказывается
            // за экраном (особенно при старте с автопродолжением).
            DispatcherQueue.TryEnqueue(async () => await ScrollSelectedChannelIntoViewAsync());
        };
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
                // Таймер сна — осознанный выход, не сворачивание в трей.
                App.AllowClose = true;
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
            ViewModel.PortalEpisodePickRequested -= OnPortalEpisodePickRequested;
            ViewModel.VodResumePromptRequested -= OnVodResumePromptRequested;
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
            Player.RefreshVodPosition();
            UpdateVodSeekBar();
            // Позиция VOD для предложения «продолжить с места остановки»
            // при следующем открытии фильма.
            ViewModel.CaptureVodPosition();
            // Обновление текста StatsOverlay под курсором порождает
            // синтетические PointerMoved — input-site возвращал стрелку
            // (мелькание). Пока курсор спрятан, текст заморожен.
            if (!_cursorHidden)
            {
                UpdateStatsOverlay();
            }
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
                // Идущие записи запоминаем, чтобы предложить продолжение
                // оставшейся части при следующем запуске (передача могла не
                // кончиться). URL не храним — подписи истекают, при
                // продолжении возьмём свежий из плейлиста по имени канала.
                ViewModel.AppSettings.InterruptedRecordings = ViewModel.Recording.Active
                    .Select(r => new Models.InterruptedRecording
                    {
                        ChannelName = r.ChannelName,
                        ProgramName = r.ChannelName,
                        EndTime = r.DurationSec is > 0
                            ? r.StartedAt.AddSeconds(r.DurationSec.Value)
                            : null
                    })
                    .ToList();

                ViewModel.SaveSettingsAsync().GetAwaiter().GetResult();

                // Идущая запись останавливается — файл остаётся валидным TS
                // (Kill процесса = обрыв потока, MPEG-TS переживает это).
                ViewModel.Recording.StopAll();

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

            // Иконка в трее (если выход пошёл мимо Closing, например по
            // Exit-пути) и буферы Serilog — до немедленного Environment.Exit.
            App.Tray?.Dispose();
            App.Tray = null;
            Serilog.Log.CloseAndFlush();

            Environment.Exit(0);
        };
    }

    /// <summary>
    /// Вызывается при навигации на эту страницу. Принимает плейлист из Hub Page
    /// или данные для VOD resume (плейлист + название канала + индекс серии).
    /// </summary>
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is PlaylistSource playlist)
        {
            _navigatedPlaylist = playlist;
            _cameFromHub = true;
            Serilog.Log.Information("OnNavigatedTo: получен плейлист Id={Id} Name={Name} IsPortal={IsPortal}",
                playlist.Id, playlist.Name, playlist.IsPortal);
        }
        else if (e.Parameter is ValueTuple<PlaylistSource, string, int> tuple)
        {
            _navigatedPlaylist = tuple.Item1;
            _vodResumeChannelTitle = tuple.Item2;
            _vodResumeEpisodeIndex = tuple.Item3;
            _cameFromHub = true;
            Serilog.Log.Information("OnNavigatedTo: VOD resume, плейлист Id={Id} Name={Name} IsPortal={IsPortal} Title={Title} Ep={Ep}",
                tuple.Item1.Id, tuple.Item1.Name, tuple.Item1.IsPortal, tuple.Item2, tuple.Item3);
        }
        else if (e.Parameter is ValueTuple<PlaylistSource, bool> loadTuple)
        {
            _navigatedPlaylist = loadTuple.Item1;
            _skipResume = loadTuple.Item2;
            _cameFromHub = true;
            Serilog.Log.Information("OnNavigatedTo: загрузка из Hub, skipResume={Skip}, плейлист Id={Id} Name={Name}",
                loadTuple.Item2, loadTuple.Item1.Id, loadTuple.Item1.Name);
        }
        else
        {
            Serilog.Log.Information("OnNavigatedTo: параметр = {Param}", e.Parameter?.ToString() ?? "NULL");
        }
    }

    /// <summary>
    /// Продолжение записей, прерванных закрытием приложения: для каждой
    /// незаконченной (EndTime в будущем) и находимой в текущем плейлисте —
    /// один диалог «Продолжить запись?». Продолжение пишет ffmpeg в НОВЫЙ
    /// файл «… (продолжение)» на оставшееся время, со свежим URL потока
    /// (старый к моменту запуска истёк по подписи).
    /// </summary>
    private async Task OfferInterruptedRecordingsAsync()
    {
        try
        {
            var now = DateTime.Now;
            var resumable = ViewModel.AppSettings.InterruptedRecordings
                .Where(r => r.EndTime == null || r.EndTime > now)
                .ToList();

            // Отработавшие своё и ненайденные каналы убираем из списка в
            // любом случае.
            ViewModel.AppSettings.InterruptedRecordings = resumable
                .Where(r => ViewModel.Channels.Any(c =>
                    string.Equals(c.Name, r.ChannelName, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            await ViewModel.SaveSettingsAsync();

            if (ViewModel.AppSettings.InterruptedRecordings.Count == 0)
            {
                return;
            }

            var names = string.Join(Environment.NewLine, ViewModel.AppSettings.InterruptedRecordings
                .Select(r => $"• {r.ChannelName}" +
                             (r.EndTime != null ? string.Format(L.T("Do_Vremeni_0"), $"{r.EndTime:HH:mm}") : "")));
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = L.T("Prervannaya_Zapis"),
                Content = new TextBlock
                {
                    Text = string.Format(L.T("Pri_Proshlom_Zakrytii_Prilozheniya_Prervalas_Zapis"), Environment.NewLine, names, Environment.NewLine, Environment.NewLine, Environment.NewLine, names, Environment.NewLine, Environment.NewLine),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = L.T("Prodolzhit"),
                CloseButtonText = L.T("Net")
            };
            var resume = await dialog.ShowAsync();

            var toResume = ViewModel.AppSettings.InterruptedRecordings.ToList();
            ViewModel.AppSettings.InterruptedRecordings.Clear();
            await ViewModel.SaveSettingsAsync();

            if (resume != ContentDialogResult.Primary)
            {
                return;
            }

            foreach (var rec in toResume)
            {
                var channel = ViewModel.Channels.FirstOrDefault(c =>
                    string.Equals(c.Name, rec.ChannelName, StringComparison.OrdinalIgnoreCase));
                if (channel == null || string.IsNullOrWhiteSpace(channel.StreamUrl))
                {
                    continue;
                }

                var remaining = rec.EndTime != null
                    ? (int)Math.Max(60, (rec.EndTime.Value - DateTime.Now).TotalSeconds)
                    : (int?)null;
                ViewModel.Recording.Start(
                    channel.StreamUrl,
                    string.Format(L.T("Prodolzhenie"), rec.ChannelName),
                    rec.ChannelName,
                    remaining,
                    ViewModel.AppSettings.RecordingsFolder);
                _logger.LogInformation(
                    "Продолжена прерванная запись: {Channel}, осталось {Remaining} c.",
                    rec.ChannelName, remaining?.ToString() ?? "до остановки");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Предложение продолжения прерванных записей.");
        }
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
        Serilog.Log.Information("InitializeAsync: ActivePlaylistId из настроек = {Id}, плейлистов = {Count}",
            savedSettings.ActivePlaylistId, savedSettings.Playlists.Count);

        // Позиции досмотра VOD — из кэш-БД (с миграцией старых из settings.json).
        await ViewModel.LoadVodResumePositionsAsync();

        var initialChannels = new List<ChannelViewModel>();

        // Язык и тема — до построения любого UI-текста.
        ApplyTheme(savedSettings.Theme);
        ApplyInitialState();

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

        // Пресет улучшения картинки — отметка в меню кнопки и режим для
        // всех открываемых далее потоков (считывается в StartPlaybackAsync).
        Player.VideoUpscalerMode = VideoUpscaler.Normalize(savedSettings.VideoUpscaler);

        // Рендер-апскейл (frame server): если включён в прошлой сессии —
        // показываем панель; рендер привяжется при PlayerChanged первого
        // запуска потока.
        FrameServerPanel.Visibility = savedSettings.FrameServerRender
            ? Visibility.Visible
            : Visibility.Collapsed;
        VideoOverlayFrameServerItem.IsChecked = savedSettings.FrameServerRender;
        OverlayFrameServerItem.IsChecked = savedSettings.FrameServerRender;

        // Оверлей статистики — если был включён в прошлой сессии.
        if (savedSettings.StatsOverlayVisible)
        {
            SetStatsOverlayVisible(show: true, persist: false);
        }

        // Миграция с одной версии настроек: единственный PlaylistUrl прошлых
        // версий становится первым плейлистом списка (Id=1 — под него же
        // мигрируется и старая запись кэша плейлиста).
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

        // Активный плейлист: приоритет у переданного из Hub Page.
        _activePlaylist = _navigatedPlaylist
            ?? ViewModel.AppSettings.Playlists
                .FirstOrDefault(p => p.Id == ViewModel.AppSettings.ActivePlaylistId)
            ?? ViewModel.AppSettings.Playlists.FirstOrDefault();
        Serilog.Log.Information("InitializeAsync: _activePlaylist Id={Id} Name={Name} IsPortal={IsPortal}",
            _activePlaylist?.Id ?? -1, _activePlaylist?.Name ?? "NULL", _activePlaylist?.IsPortal ?? false);
        if (_activePlaylist != null)
        {
            ViewModel.AppSettings.ActivePlaylistId = _activePlaylist.Id;
            initialChannels.AddRange(await LoadPlaylistChannelsAsync(_activePlaylist));
        }

        // Id назначаются один раз для обоих путей появления каналов (скачанный
        // плейлист или кэш) — до этого ChannelViewModel.Id может быть default.
        // Очищаем singleton-репозиторий: при навигации Hub→MainPage→Hub→MainPage
        // старые каналы из предыдущего плейлиста остались бы в репозитории.
        await _channelRepository.Clear();
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

        // Показываем кнопку "Назад" если пришли из Hub Page
        if (_cameFromHub)
        {
            BackToHubButton.Visibility = Visibility.Visible;
            ChannelsHeaderText.Visibility = Visibility.Collapsed;
        }

        UpdatePlaylistMenu();

        // Полуавтоматическое обновление: фоновая проверка через пару минут
        // после старта (не чаще раза в сутки), см. RunAutoUpdateCheckAsync.
        ScheduleAutoUpdateCheck();
        ApplyChannelViewMode();

        // Записи, прерванные прошлым закрытием: если передача ещё идёт —
        // предлагаем продолжить запись оставшейся части.
        _ = OfferInterruptedRecordingsAsync();

        // Выбираем первый канал ДО запуска загрузки EPG: SelectedChannel
        // нужен для x:Bind EPG-панели, и раньше он назначался только после
        // полной загрузки EPG (при первом скачивании 45 МБ фида это десятки
        // секунд) — всё это время панель программ оставалась пустой.
        // Если в настроек есть последний смотренный канал — выбираем его
        // и автопродолжаем воспроизведение (fire-and-forget: старт и так
        // происходит в фоне, EPG грузится дальше независимо).
        //
        // При загрузке из Hub через "Загрузить плейлист" или "Загрузить портал"
        // (_cameFromHub + нет VOD-резюма) — НЕ применяем последний канал,
        // чтобы пользователь начинал с чистого списка.
        var autoResume = !_skipResume && _vodResumeChannelTitle == null;
        if (ViewModel.Channels.Count > 0 && autoResume)
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
        else if (ViewModel.Channels.Count > 0)
        {
            ViewModel.SelectedChannel = ViewModel.Channels[0];
        }

        // VOD resume из Hub Page: если пришли с конкретным фильмом/серией
        if (_vodResumeChannelTitle != null)
        {
            _ = ResumeVodFromHubAsync(_vodResumeChannelTitle, _vodResumeEpisodeIndex);
        }

        // Даем UI отрисовать список каналов до старта загрузки EPG. Без
        // этого ListView мог получить коллекцию, но не успеть отрисоваться
        // до первого await внутри LoadEPGAsync — и на экране список
        // появлялся только вместе с EPG.
        await Task.Yield();

        // Загрузка EPG больше не блокирует UI (тяжёлая работа в пуле
        // потоков — см. EpgCacheStore и EPGService.MergeSources), а
        // программы догружаются в панели по мере готовности.
        await ViewModel.EpgViewModel.LoadEPGAsync();

        // Загружаем полный EPG (список передач) для выбранного канала,
        // чтобы панель EPG не была пустой при старте.
        if (ViewModel.SelectedChannel is { } selected)
        {
            await ViewModel.EpgViewModel.LoadEPGForChannelAsync(selected.Id);
        }

        // После (пере)загрузки EPG коллекции передач пересобраны — возвращаем
        // колокольчики активных напоминаний.
        ViewModel.ApplyReminderFlags();
    }

    // ===================== Полуавтоматическое обновление =====================

    /// <summary>
    /// Скачанный установщик, ожидающий окончания записей: установка не
    /// запускается, пока идёт хотя бы одна запись (прерывать нельзя).
    /// </summary>
    private string? _pendingUpdateSetupPath;

    /// <summary>
    /// Планирует фоновую проверку обновлений: через 2 минуты после старта,
    /// не чаще раза в сутки (LastUpdateCheckUtc), только если включена в
    /// настройках. Ошибки сети/скачивания полностью тихие — старая версия
    /// продолжает работать, проверка повторится при следующем запуске.
    /// </summary>
    private void ScheduleAutoUpdateCheck()
    {
        if (!ViewModel.AppSettings.AutoUpdateEnabled)
        {
            return;
        }

        if (ViewModel.AppSettings.LastUpdateCheckUtc is { } last &&
            DateTime.UtcNow - last < TimeSpan.FromHours(20))
        {
            return;
        }

        _ = RunAutoUpdateCheckAsync();
    }

    private async Task RunAutoUpdateCheckAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2));

            // Пользователь мог выключить тумблер, пока шла задержка.
            if (!ViewModel.AppSettings.AutoUpdateEnabled)
            {
                return;
            }

            var update = await _updateService.CheckForUpdateAsync();

            ViewModel.AppSettings.LastUpdateCheckUtc = DateTime.UtcNow;
            await _settingsService.SaveAsync(ViewModel.AppSettings);
            if (update == null)
            {
                return;
            }

            var setupPath = await _updateService.DownloadAsync(update);
            DispatcherQueue.TryEnqueue(() => _ = OfferUpdateInstallAsync(update.Version, setupPath));
        }
        catch (Exception ex)
        {
            // Обновление — не критичная функция: любая ошибка (сеть, сумма,
            // диск) оставляет текущую версию работающей, попытка повторится
            // при следующем запуске.
            _logger.LogWarning(ex, "Автообновление: шаг не удался, текущая версия продолжает работать.");
        }
    }

    /// <summary>
    /// Диалог «установить сейчас?»: согласие → тихая установка (или откладывание,
    /// если идут записи — установится автоматически после последней), отказ —
    /// ничего не делаем, установщик остаётся во временной папке.
    /// </summary>
    private async Task OfferUpdateInstallAsync(Version version, string setupPath)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = L.T("Dostupno_Obnovlenie"),
            Content = string.Format(L.T("Versiya_0_Skachana_Ustanovit_Seychas_Prilozhenie"), version, version),
            PrimaryButtonText = L.T("Ustanovit_Seychas"),
            CloseButtonText = L.T("Pozzhe"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            _logger.LogInformation("Обновление {Version}: пользователь отложил установку.", version);
            return;
        }

        if (ViewModel.Recording.Active.Count > 0)
        {
            // Установить нельзя, пока идут записи — откладываем до окончания
            // последней (согласие уже получено, повторного вопроса не будет).
            _pendingUpdateSetupPath = setupPath;
            ViewModel.Recording.RecordingsChanged += OnRecordingsChanged_InstallUpdate;
            _logger.LogInformation(
                "Обновление {Version} отложено: идут записи ({Count}), установится после их окончания.",
                version, ViewModel.Recording.Active.Count);

            var info = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = L.T("Obnovlenie_Otlozheno"),
                Content = L.T("Idet_Zapis_Peredach_Obnovlenie_Ustanovitsya_Avtomaticheski"),
                CloseButtonText = L.T("Ponyatno")
            };
            await info.ShowAsync();
            return;
        }

        _updateService.RunInstallerAndExit(setupPath);
    }

    private void OnRecordingsChanged_InstallUpdate(object? sender, EventArgs e)
    {
        if (_pendingUpdateSetupPath == null || ViewModel.Recording.Active.Count > 0)
        {
            return;
        }

        // Последняя запись завершилась — ставим отложенное обновление.
        ViewModel.Recording.RecordingsChanged -= OnRecordingsChanged_InstallUpdate;
        var setupPath = _pendingUpdateSetupPath;
        _pendingUpdateSetupPath = null;
        DispatcherQueue.TryEnqueue(() => _updateService.RunInstallerAndExit(setupPath));
    }

    /// <summary>
    /// Вид списка каналов/каталога: строки ↔ сетка постеров (настройка
    /// ChannelListPosterView). Скрывает один контейнер и показывает другой;
    /// иконка кнопки отражает текущий вид.
    /// </summary>
    private void ApplyChannelViewMode()
    {
        // Постер-вид доступен только для портала; на M3U — всегда список.
        var posters = ViewModel.IsContentTypeFilterVisible == Visibility.Visible && ViewModel.AppSettings.ChannelListPosterView;
        PosterGridView.Visibility = posters ? Visibility.Visible : Visibility.Collapsed;
        ChannelsListView.Visibility = posters ? Visibility.Collapsed : Visibility.Visible;
        PosterViewIconList.Visibility = posters ? Visibility.Collapsed : Visibility.Visible;
        PosterViewIconList2.Visibility = posters ? Visibility.Collapsed : Visibility.Visible;
        PosterViewIconList3.Visibility = posters ? Visibility.Collapsed : Visibility.Visible;
        PosterViewIconGrid.Visibility = posters ? Visibility.Visible : Visibility.Collapsed;

        // Скрытый вид отсоединяем от данных: невидимый ItemsControl всё равно
        // обрабатывает смены ItemsSource (а на 20k+ элементов это заметно).
        // Очередная смена DisplayedChannels вернёт источник через x:Bind.
        if (posters)
        {
            ChannelsListView.ItemsSource = null;
            PosterGridView.ItemsSource = ViewModel.DisplayedChannels;
        }
        else
        {
            PosterGridView.ItemsSource = null;
            ChannelsListView.ItemsSource = ViewModel.DisplayedChannels;
        }
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
    /// <summary>
    /// Начальное состояние динамических элементов плеера/оверлеев (язык
    /// фиксируется при старте через x:Uid + MRT, тексты здесь не ставятся).
    /// </summary>
    private void ApplyInitialState()
    {
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
        // Пересборка сгруппированного источника на 20k+ элементов дорогая —
        // делаем её только когда полноэкранный оверлей реально виден; при
        // входе в fullscreen SetFullScreenMode вызывает этот метод явно.
        if (FullScreenOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

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

            var key = string.IsNullOrWhiteSpace(channel.Group) ? L.T("Bez_Gruppy") : channel.Group!.Trim();
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

        // Назначение нового ItemsSource сбрасывает выделение — восстанавливаем
        // текущий канал, иначе в fullscreen теряется подсветка играющего.
        // TwoWay-биндинг SelectedItem сам не сработает: значение на пути
        // (ViewModel.SelectedChannel) при этом не менялось.
        if (ViewModel.SelectedChannel is { } selected)
        {
            OverlayChannelsListView.SelectedItem = selected;
        }

        Serilog.Log.Debug(
            "OverlayList: пересборка — каналов {Channels}, групп {Groups}, выделен «{Selected}», в списке выделение: {HasSelection}",
            ViewModel.DisplayedChannels.Count, orderedGroups.Count,
            ViewModel.SelectedChannel?.Name,
            OverlayChannelsListView.SelectedItem != null);
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
            Title = L.T("Dobavit_Kanal"),
            PrimaryButtonText = L.T("Dobavit_Lbl"),
            CloseButtonText = L.T("Otmena_Lbl"),
            XamlRoot = ((Button)sender).XamlRoot
        };

        var panel = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Vertical, Spacing = 12, Width = 300 };
        var nameBox = new TextBox { Header = L.T("Nazvanie"), PlaceholderText = L.T("Vvedite_Nazvanie_Kanala") };
        var urlBox = new TextBox { Header = L.T("URL_Potoka"), PlaceholderText = L.T("Vvedite_URL_Potoka") };
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
    // ===================== Делегаты к PlayerViewModel =====================

    private Task PlayLiveAsync(ChannelViewModel channel) => ViewModel.PlayChannelAsync(channel, interactive: false);

    private void StopPlayback() => Player.Stop();

    private void EPGProgramsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EPGEntry entry)
        {
            ViewModel.PlayArchiveEntryCommand.Execute(entry);
        }
    }

    // ===================== Выбор канала в списке/сетке =====================

    /// <summary>
    /// Защита от петли: OneWay-привязка SelectedItem толкает выделение в
    /// контролы, их SelectionChanged не должен писать обратно то же значение.
    /// </summary>
    private bool _syncingListSelection;

    /// <summary>
    /// Выбор в списке каналов/сетке постеров → SelectedChannel. Замена TwoWay
    /// привязки: TwoWay затирал SelectedChannel в null при очистке ItemsSource
    /// скрытого вида (переключение список↔постеры) — видео исчезало.
    /// </summary>
    private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingListSelection)
        {
            return;
        }

        if (sender is Microsoft.UI.Xaml.Controls.Primitives.Selector { SelectedItem: ChannelViewModel channel } &&
            !ReferenceEquals(channel, ViewModel.SelectedChannel))
        {
            ViewModel.SelectedChannel = channel;
        }
    }

}
