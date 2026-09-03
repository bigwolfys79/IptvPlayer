using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more project templates, see: http://aka.ms/winui-project-info.

namespace IptvPlayer;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// Здесь же composition root приложения: конфигурация Serilog (до всего, что
/// может логировать) и DI-контейнер Microsoft.Extensions.DependencyInjection,
/// из которого страницы резолвят сервисы и ViewModel'ы (см. Services).
/// </summary>
public partial class App : Application
{
    private static Window? _window;

    /// <summary>
    /// Глобальный DI-контейнер. Заполняется в конструкторе App (до создания
    /// окна) и живёт до конца процесса. Страницы/окна берут зависимости через
    /// App.Services.GetRequiredService — WinUI не даёт внедрять их в
    /// конструкторы XAML-элементов, это стандартный для WinUI 3 паттерн.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>Каталог файлового лога (%LocalAppData%\IptvPlayer\logs).</summary>
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer", "logs");

    /// <summary>
    /// Главное окно приложения. Нужно диалогам для FileOpenPicker/
    /// FileSavePicker: в WinUI 3 пикер без HWND-владельца (InitializeWithWindow)
    /// бросает исключение, а получить окно из XamlRoot нельзя.
    /// </summary>
    public static Window? MainWindow => _window;

    /// <summary>
    /// Разрешить настоящее закрытие окна: обычный крестик сворачивает в
    /// трей (CloseToTray), реальный выход — через меню иконки в трее или
    /// это явно запрошенное закрытие (таймер сна, shutdown).
    /// </summary>
    public static bool AllowClose;

    /// <summary>Иконка в трее (null, пока не создана). Убирается при выходе.</summary>
    public static Services.TrayIconService? Tray { get; set; }

    // Уровень "выше Fatal": ни одно событие Serilog через него не проходит —
    // так выключается файловый лог без пересоздания логгера.
    private const LogEventLevel FileLoggingDisabledLevel = (LogEventLevel)100;

    // Переключатель видит только файловый sink: вывод в Debug (окно Output
    // под отладчиком) остаётся всегда, настройка управляет записью на диск.
    private static readonly LoggingLevelSwitch FileLogSwitch = new(LogEventLevel.Information);

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        // Serilog конфигурируется ДО InitializeComponent и подписки на
        // исключения: всё, что логируется дальше (включая краши на старте),
        // уже попадает и в файл, и в Debug-вывод. Начальное состояние
        // файлового лога берём из настроек — иначе при выключенной настройке
        // каждый запуск создавал бы файл хотя бы ради пары стартовых строк.
        // SettingsService читает локальный JSON синхронно (Task.FromResult),
        // так что блокировки UI-потока здесь нет; контейнер ещё не построен,
        // поэтому одноразовый экземпляр с NullLogger.
        AppSettings initialSettings;
        try
        {
            initialSettings = new SettingsService(NullLogger<SettingsService>.Instance)
                .LoadAsync().GetAwaiter().GetResult();
        }
        catch
        {
            initialSettings = new AppSettings();
        }
        // Язык применяется к MRT-контексту до InitializeComponent: x:Uid
        // тексты фиксируются при разборе XAML, поэтому локализация целиком
        // выбирается на старте (смена в настройках — после перезапуска).
        L.SetLanguage(initialSettings.Language);
        TempDiagnosticsEnabled = initialSettings.TempDiagnosticsEnabled;
        FileLogSwitch.MinimumLevel = initialSettings.FileLoggingEnabled
            ? LogEventLevel.Information
            : FileLoggingDisabledLevel;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            // Замена прежнего Debug.WriteLine в Services.Logger: видно в
            // Output только под отладчиком, в Release компилируется в пустоту.
            .WriteTo.Debug(outputTemplate: OutputTemplate)
            // Ежедневный роллинг + предел размера и срока: прежний ручной
            // роллинг (5 МБ -> *.old) не ограничивал общее место на диске.
            .WriteTo.File(
                Path.Combine(LogDirectory, "iptvplayer-.log"),
                levelSwitch: FileLogSwitch,
                outputTemplate: OutputTemplate,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14,
                encoding: System.Text.Encoding.UTF8)
            .CreateLogger();

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        InitializeComponent();

        // Раньше необработанные исключения (например, из async void
        // обработчиков кнопок) просто "глотались" рантаймом или ловились
        // отладчиком на бесполезной генерируемой строке без деталей —
        // в Output было видно только загрузку сборок, самой ошибки не было
        // видно вообще. Теперь она гарантированно попадает в лог, даже без
        // подключённого отладчика.
        UnhandledException += OnUnhandledException;

        // UnhandledException выше ловит исключения, ТОЛЬКО если они долетели
        // обратно до UI-потока. Исключение из настоящего фонового потока
        // (например, из Task.Run или из продолжения, которое никто не
        // заawait'ил) до него не долетает и валит процесс молча, без единой
        // строчки в логе — это отдельный и вполне вероятный источник крашей,
        // не связанных с логикой конкретной кнопки. Эти два хендлера —
        // подстраховка именно для таких случаев: сам краш они не остановят
        // (для AppDomain.UnhandledException это в принципе невозможно —
        // после него процесс всё равно завершится), но успевают записать
        // причину в файл до этого, чего раньше не было вообще.
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    // Формат прежнего самописного логгера (Services.Logger), включая скобки
    // [уровня] и [источника] — чтобы старые привычки grep по логу работали.
    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Включает/выключает файловый лог на лету (тумблер в настройках).
    /// Работает через LoggingLevelSwitch файлового sink'а — без пересоздания
    /// логгера и потери событий на переключении.
    /// </summary>
    /// <summary>
    /// Включена ли «Временная диагностика» (см. AppSettings.TempDiagnosticsEnabled):
    /// глушение необработанных исключений + подробный лог EPG по каналам.
    /// Статическое: App.OnUnhandledException и статические ветки EPGService
    /// не имеют доступа к DI-контейнеру в момент срабатывания.
    /// </summary>
    public static bool TempDiagnosticsEnabled { get; set; }

    public static void SetFileLoggingEnabled(bool enabled)
    {
        if (!enabled)
        {
            Log.Information("Файловый лог выключен в настройках.");
        }
        FileLogSwitch.MinimumLevel = enabled ? LogEventLevel.Information : FileLoggingDisabledLevel;
        if (enabled)
        {
            Log.Information("Файловый лог включён в настройках.");
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // ILogger<T> из Microsoft.Extensions.Logging поверх статически
        // сконфигурированного Serilog: SourceContext = имя класса, уровень
        // и форматы событий — общие с Log.* из статического контекста.
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSerilog(dispose: false);
        });

        // Сервисы — singletons: приложение с одним окном, а ChannelRepository
        // и EPGService — разделяемое состояние, которое и раньше существовало
        // в одном экземпляре (создавалось вручную в MainPage и раздавалось
        // дальше). Конкретные типы регистрируются отдельно от интерфейсов,
        // потому что MainPage работает с ChannelRepository/EPGService как с
        // конкретными типами.
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IXmlTvService, XmlTvService>();
        services.AddSingleton<LocalStreamProxy>();
        services.AddSingleton<IStreamService, StreamService>();
        services.AddSingleton<IPlaylistCacheService, PlaylistDatabaseService>();
        services.AddSingleton<IM3UParserService, M3UParserService>();
        services.AddSingleton<IVideoPortalService, VideoPortalService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<ChannelRepository>();
        services.AddSingleton<IChannelRepository>(sp => sp.GetRequiredService<ChannelRepository>());
        services.AddSingleton<EPGService>();
        services.AddSingleton<IEPGService>(sp => sp.GetRequiredService<EPGService>());
        services.AddSingleton<RecordingService>();

        // ViewModel'ы — тоже singletons: MainPage создаётся один раз за
        // сессию, а EpgViewModel держит состояние (список каналов, окно EPG).
        services.AddSingleton<EpgViewModel>();
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<MainPageViewModel>();
        services.AddSingleton<VodResumeStore>();
        services.AddSingleton<LocalVideoFileService>();
    }

    // ===================== Страж зависания UI =====================

    // Сердцебиение UI-потока: DispatcherTimer тикает только пока поток
    // жив. Фоновый System.Threading.Timerwatchdog сравнивает счётчик:
    // не менялся дольше 10 с — UI-поток заблокирован чем-то синхронным
    // (было дважды: Windows закрывала приложение как «не отвечающее»,
    // в логе при этом ни одной строчки — теперь вис будет виден).
    private long _uiHeartbeat;
    private System.Threading.Timer? _uiHangTimer;
    private DateTime _lastHeartbeatUtc = DateTime.UtcNow;
    private bool _hangAnnounced;

    private void StartUiHangWatchdog()
    {
        var heartbeatTimer = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        heartbeatTimer.Tick += (_, _) => _uiHeartbeat++;
        heartbeatTimer.Start();

        var lastSeen = 0L;
        _uiHangTimer = new System.Threading.Timer(_ =>
        {
            var beat = Interlocked.Read(ref _uiHeartbeat);
            if (beat != lastSeen)
            {
                lastSeen = beat;
                var now = DateTime.UtcNow;
                if (_hangAnnounced)
                {
                    _hangAnnounced = false;
                    Log.Information("UI-поток отвечал снова (простой {Seconds:F0} с).",
                        (now - _lastHeartbeatUtc).TotalSeconds);
                }
                _lastHeartbeatUtc = now;
                return;
            }

            var staleSeconds = (DateTime.UtcNow - _lastHeartbeatUtc).TotalSeconds;
            if (staleSeconds >= 10)
            {
                // Не спамим: одно объявление на эпизод + напоминание раз в 30 с.
                if (!_hangAnnounced || staleSeconds % 30 < 3)
                {
                    Log.Fatal("UI-поток НЕ ОТВЕЧАЕТ {Seconds:F0} с — зависание. " +
                        "Пул потоков: {WorkerBusy}/{WorkerTotal} занято, очередь ThreadPool: {QueueLength}.",
                        staleSeconds,
                        System.Threading.ThreadPool.PendingWorkItemCount,
                        System.Threading.ThreadPool.ThreadCount);
                }
                _hangAnnounced = true;
            }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // ВАЖНО: CloseAndFlush только в самом конце — раньше слепок дерева
        // при LayoutCycle писался уже в закрытый логгер и терялся.
        Log.Error(e.Exception, "Необработанное исключение UI-потока (App.UnhandledException)");

        // LayoutCycleException не называет виновника — снимаем слепок
        // визуального дерева (имена + фактические размеры первых N узлов):
        // по нему видно, какие панели были на экране и с какими размерами
        // в момент цикла компоновки.
        if (e.Exception is Microsoft.UI.Xaml.LayoutCycleException && _window?.Content is FrameworkElement root)
        {
            try
            {
                var snapshot = new System.Text.StringBuilder();
                var queue = new Queue<(DependencyObject Node, string Path)>();
                queue.Enqueue((root, root.Name));
                var visited = new HashSet<DependencyObject>();
                int dumped = 0;
                while (queue.Count > 0 && dumped < 300)
                {
                    var (node, path) = queue.Dequeue();
                    if (!visited.Add(node))
                    {
                        continue;
                    }

                    if (node is FrameworkElement fe)
                    {
                        dumped++;
                        snapshot.AppendLine(string.Format(
                            "  {0} [{1}] {2:F0}x{3:F0} vis={4}",
                            string.IsNullOrEmpty(path) ? "<anon>" : path,
                            fe.GetType().Name,
                            fe.ActualWidth, fe.ActualHeight, fe.Visibility));
                    }

                    var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
                    for (var i = 0; i < count; i++)
                    {
                        var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i);
                        var childPath = node is FrameworkElement parent && !string.IsNullOrEmpty(parent.Name)
                            ? parent.Name
                            : path;
                        queue.Enqueue((child, childPath));
                    }
                }
                Log.Error("Слепок визуального дерева при LayoutCycle ({Count} узлов):\n{Snapshot}", dumped, snapshot);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Не удалось снять слепок дерева при LayoutCycle.");
            }
        }

        // Помечаем как обработанное, чтобы приложение не падало/не зависало
        // молча — только пока включена «Временная диагностика» (меню
        // шестерёнки → Диагностика). После подтверждения, что зависания
        // починены, переключатель и этот код можно удалить.
        e.Handled = TempDiagnosticsEnabled;
        Serilog.Log.CloseAndFlush();
    }

    /// <summary>
    /// Второй запуск переадресовал сюда активацию: показываем окно
    /// (в том числе когда оно свернуто в трей) и выводим на передний план.
    /// Событие приходит в контексте WinAppSDK — marshaling в UI-поток.
    /// </summary>
    /// <summary>Второй экземпляр передаёт видеофайл через этот файл.</summary>
    private static string PendingVideoFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer", "pending_video.txt");

    /// <summary>Видеофайл в аргументах командной строки (ассоциация «Открыть с помощью»), null — нет.</summary>
    internal static string? GetCommandLineVideoFile()
    {
        var args = Environment.GetCommandLineArgs();
        return args.Skip(1)
            .Select(a => { try { return Path.GetFullPath(a); } catch { return null; } })
            .FirstOrDefault(p => p != null && File.Exists(p) && LocalVideoFileService.IsVideoFile(p));
    }

    private void OnInstanceActivated(object? sender, Microsoft.Windows.AppLifecycle.AppActivationArguments e)
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_window is MainWindow mainWindow)
            {
                mainWindow.ShowFromTray();

                // Второй экземпляр был запущен открытием видеофайла —
                // играем его в работающем приложении.
                try
                {
                    if (File.Exists(PendingVideoFilePath))
                    {
                        var videoPath = File.ReadAllText(PendingVideoFilePath).Trim();
                        File.Delete(PendingVideoFilePath);
                        if (File.Exists(videoPath) && LocalVideoFileService.IsVideoFile(videoPath))
                        {
                            var file = LocalVideoFileService.FromPath(videoPath);
                            mainWindow.AppFrame.Navigate(typeof(MainPage), file);
                            Log.Information("Открыт видеофайл из проводника: {File}", videoPath);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Открытие видеофайла из проводника не удалось.");
                }

                Log.Information("Активация переадресована: окно восстановлено из трея.");
            }
        });
    }

    private void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        if (ex != null)
        {
            Log.Fatal(ex, "Необработанное исключение фонового потока (AppDomain.UnhandledException, приложение сейчас упадёт)");
        }
        else
        {
            Log.Fatal("Необработанное исключение фонового потока (AppDomain.UnhandledException, приложение сейчас упадёт): {ExceptionObject}",
                e.ExceptionObject?.ToString() ?? "unknown exception object");
        }
    }

    private void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Необработанное исключение задачи (TaskScheduler.UnobservedTaskException)");
        e.SetObserved();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // === ЕДИНСТВЕННЫЙ ЭКЗЕМПЛЯР ===
        // Повторный запуск (окно в трее — процесс жив) не создаёт второй
        // экземпляр: активация переадресуется работающему, и он поднимает
        // окно. Раньше параллельные экземпляры дрались за settings.json
        // (IOException при сохранении, затем затирание настроек дефолтами).
        // Проверка до всего остального: второй процесс завершается молча.
        var instance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("IptvPlayer.Main");
        if (!instance.IsCurrent)
        {
            // Если второй экземпляр запущен открытием видеофайла (ассоциация
            // в проводнике) — передаём путь первому через pending-файл:
            // RedirectActivationToAsync не переносит произвольные аргументы.
            var redirectedVideo = GetCommandLineVideoFile();
            if (redirectedVideo != null)
            {
                try
                {
                    Directory.CreateDirectory(LogDirectory);
                    File.WriteAllText(PendingVideoFilePath, redirectedVideo);
                    Log.Information("Переадресация видеофайла работающему экземпляру: {File}", redirectedVideo);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Не удалось записать pending-файл видеофайла.");
                }
            }

            Log.Information("Уже запущен другой экземпляр — переадресация активации и выход.");
            try
            {
                var activationArgs = instance.GetActivatedEventArgs();
                if (activationArgs != null)
                {
                    await instance.RedirectActivationToAsync(activationArgs);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Переадресация активации не удалась.");
            }
            Log.CloseAndFlush();
            Environment.Exit(0);
            return;
        }
        instance.Activated += OnInstanceActivated;

        // Отладочные дампы запросов/ответов портала (portal_dump) писались
        // прежними версиями и содержали прямые ссылки с токенами доступа —
        // удаляем накопленное, дамп больше не ведётся.
        try
        {
            Directory.Delete(Path.Combine(LogDirectory, "..", "portal_dump"), recursive: true);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Нет папки или файл занят — не препятствие для запуска.
        }

        // === ПРОВЕРКА ЛИЦЕНЗИИ ДО СОЗДАНИЯ ОКНА ===
        var license = LicenseService.CheckLicense();
        Log.Information("OnLaunched: UsageType={Type}, DaysRemaining={Days}, IsExpired={Expired}",
            license.UsageType, license.DaysRemaining, license.IsExpired);

        if (license.IsExpired)
        {
            // Минимальное окно только для показа диалога
            _window = new MainWindow();
            _window.Activate();

            var dialog = new Dialogs.LicenseExpiredDialog();
            // Диалог содержит офлайн-активацию: пользователь может ввести
            // подписанную лицензию прямо здесь, тогда запускаем приложение.
            var activated = await dialog.ShowAsync(_window.Content.XamlRoot, license.DaysRemaining);

            if (!activated)
            {
                Log.Information("Пробный период истёк — приложение завершено.");
                Log.CloseAndFlush();
                Environment.Exit(0);
                return;
            }

            Log.Information("Лицензия активирована из диалога — продолжаем запуск.");
        }

        _window = new MainWindow();
        (_window as MainWindow)?.RestorePlacement();
        _window.Activate();
        StartUiHangWatchdog();

        // Синхронная выгрузка буферов Serilog при закрытии главного окна —
        // чтобы последние события гарантированно попали в файл.
        _window.Closed += (_, _) => Log.CloseAndFlush();

        // Навигация: Hub или MainPage (auto-resume). Запуск с видеофайлом
        // (ассоциация в проводнике) имеет приоритет: сразу играем файл.
        if (_window is MainWindow mainWindow)
        {
            var launchVideoFile = GetCommandLineVideoFile();
            if (launchVideoFile != null)
            {
                var videoFile = LocalVideoFileService.FromPath(launchVideoFile);
                Log.Information("OnLaunched: запуск с видеофайлом «{File}» → MainPage", launchVideoFile);
                mainWindow.AppFrame.Navigate(typeof(MainPage), videoFile);
                return;
            }

            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            var settings = settingsService.LoadAsync().GetAwaiter().GetResult();
            Log.Information("OnLaunched: ShowHubOnStartup={Hub}", settings.ShowHubOnStartup);

            var target = settings.ShowHubOnStartup ? typeof(HubPage) : typeof(MainPage);
            Log.Information("OnLaunched: навигация → {Target}", target.Name);
            try
            {
                mainWindow.AppFrame.Navigate(target);
                Log.Information("OnLaunched: навигация завершена OK");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "OnLaunched: навигация упала");
                Serilog.Log.CloseAndFlush();
                throw;
            }
        }
    }
}
