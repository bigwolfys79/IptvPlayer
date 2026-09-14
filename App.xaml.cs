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

namespace IptvPlayer;


public partial class App : Application
{
    private static Window? _window;


    public static IServiceProvider Services { get; private set; } = null!;


    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer", "logs");


    public static Window? MainWindow => _window;


    public static bool AllowClose;


    public static string? PendingUpdateSetupPath;


    public static void TryStartPendingUpdateInstall()
    {
        var setupPath = PendingUpdateSetupPath;
        PendingUpdateSetupPath = null;
        if (setupPath == null)
        {
            return;
        }

        try
        {
            if (File.Exists(setupPath))
            {
                Services.GetRequiredService<IUpdateService>().StartInstaller(setupPath);
            }
            else
            {
                Serilog.Log.Information(
                    "Отложенное обновление {Path}: файл уже удалён (чистка temp), установка пропущена.", setupPath);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Отложенное обновление: не удалось запустить установщик {Path}.", setupPath);
        }
    }


    public static Services.TrayIconService? Tray { get; set; }

    private const LogEventLevel FileLoggingDisabledLevel = (LogEventLevel)100;

    private static readonly LoggingLevelSwitch FileLogSwitch = new(LogEventLevel.Information);


    public App()
    {

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

        L.SetLanguage(initialSettings.Language);
        TempDiagnosticsEnabled = initialSettings.TempDiagnosticsEnabled;
        FileLogSwitch.MinimumLevel = initialSettings.FileLoggingEnabled
            ? LogEventLevel.Information
            : FileLoggingDisabledLevel;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()


            .WriteTo.Debug(outputTemplate: OutputTemplate)


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

        UnhandledException += OnUnhandledException;

        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";


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

        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSerilog(dispose: false);
        });

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

        services.AddSingleton<EpgViewModel>();
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<MainPageViewModel>();
        services.AddSingleton<VodResumeStore>();
        services.AddSingleton<LocalVideoFileService>();
    }

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


        Log.Error(e.Exception, "Необработанное исключение UI-потока (App.UnhandledException)");

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

        e.Handled = TempDiagnosticsEnabled;
        Serilog.Log.CloseAndFlush();
    }


    private static string PendingVideoFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer", "pending_video.txt");


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


    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {

        var instance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("IptvPlayer.Main");
        if (!instance.IsCurrent)
        {

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

        try
        {
            Directory.Delete(Path.Combine(LogDirectory, "..", "portal_dump"), recursive: true);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {

        }


        var license = LicenseService.CheckLicense();
        Log.Information("OnLaunched: UsageType={Type}, DaysRemaining={Days}, IsExpired={Expired}",
            license.UsageType, license.DaysRemaining, license.IsExpired);

        if (license.IsExpired)
        {

            _window = new MainWindow();
            _window.Activate();

            var dialog = new Dialogs.LicenseExpiredDialog();


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

        _window.Closed += (_, _) => Log.CloseAndFlush();

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
