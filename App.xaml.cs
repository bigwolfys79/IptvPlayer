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
        // Счётчик скорости загрузки (байты чтения процесса): сервисы ниже
        // берут его для PauseScope на время своих больших загрузок, а
        // MainPage — для строки «Скорость потока (изм.)» в оверлее.
        services.AddSingleton<ProcessSpeedMonitor>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ICacheService, CacheService>();
        services.AddSingleton<IXmlTvService, XmlTvService>();
        services.AddSingleton<IStreamService, StreamService>();
        services.AddSingleton<IPlaylistCacheService, PlaylistCacheService>();
        services.AddSingleton<IM3UParserService, M3UParserService>();
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
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Необработанное исключение UI-потока (App.UnhandledException)");

        // Помечаем как обработанное, чтобы приложение не падало/не зависало
        // молча — это временно, только для диагностики. После того как
        // найдём и починим причину, этот флаг можно убрать.
        e.Handled = true;
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
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        (_window as MainWindow)?.RestorePlacement();
        _window.Activate();

        // Синхронная выгрузка буферов Serilog при закрытии главного окна —
        // чтобы последние события гарантированно попали в файл.
        _window.Closed += (_, _) => Log.CloseAndFlush();
    }
}
