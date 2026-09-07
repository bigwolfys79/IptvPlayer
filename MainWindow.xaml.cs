using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using IptvPlayer.Models;
using IptvPlayer.Services;

namespace IptvPlayer;

/// <summary>
/// Окно приложения: хостит Frame со страницами. UI и логику добавлять
/// в MainPage.xaml / MainPage.xaml.cs, а не сюда — тогда доступны события
/// навигации и жизненный цикл Loaded, как у Page.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// Единственный экземпляр главного окна приложения. Используется страницами
    /// (например, MainPage), которым нужно управлять presenter'ом окна —
    /// в частности, переключать настоящий полноэкранный режим ОС.
    /// </summary>
    public static MainWindow? Instance { get; private set; }

    /// <summary>
    /// Корневой Frame для навигации между Hub Page и MainPage.
    /// </summary>
    public Frame AppFrame => RootFrame;

    /// <summary>
    /// True, если сейчас активен системный полноэкранный presenter
    /// (окно без рамки и заголовка, во весь экран).
    /// </summary>
    public bool IsOsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        AppWindow.Closing += (s, e) =>
        {
            bool closeToTray;
            try
            {
                var settings = App.Services.GetRequiredService<ISettingsService>().LoadAsync().GetAwaiter().GetResult();
                closeToTray = settings.CloseToTray;
            }
            catch
            {
                closeToTray = false;
            }

            if (!App.AllowClose && closeToTray)
            {
                e.Cancel = true;
                AppWindow.Hide();
                App.Tray?.Show();
                return;
            }

            App.TryStartPendingUpdateInstall();

            MinimizeHook?.Dispose();
            App.Tray?.Dispose();
            App.Tray = null;
        };

        MinimizeHook = new MinimizeToTrayHook(this, () =>
        {
            bool minimizeToTray;
            try
            {
                var settings = App.Services.GetRequiredService<ISettingsService>().LoadAsync().GetAwaiter().GetResult();
                minimizeToTray = settings.MinimizeToTray;
            }
            catch
            {
                minimizeToTray = false;
            }

            if (minimizeToTray)
            {
                AppWindow.Hide();
                App.Tray?.Show();
            }
        });


        App.Tray ??= new Services.TrayIconService(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            ShowFromTray,
            ExitFromTray);


    }

    private bool _miniPlayer;
    private Windows.Graphics.RectInt32 _preMiniPlacement;

    /// <summary>Включён ли режим «поверх всех окон» по Ctrl+T. Хранится
    /// отдельно от фактического состояния presenter'а: мини-плеер временно
    /// включает always-on-top, а полный экран пересоздаёт presenter — после
    /// возврата из обоих режимов включённое состояние восстанавливается.</summary>
    private bool _alwaysOnTop;

    /// <summary>Состояние always-on-top до входа в мини-плеер.</summary>
    private bool _alwaysOnTopBeforeMini;

    /// <summary>Subclass для перехвата сворачивания; поле обязательно —
    /// иначе delegate соберётся GC и wndproc упадёт.</summary>
    private Services.MinimizeToTrayHook? MinimizeHook;

    /// <summary>Активен ли компактный режим мини-плеера (always-on-top).</summary>
    public bool IsMiniPlayer => _miniPlayer;

    /// <summary>Окно сейчас поверх всех окон (по желанию пользователя или
    /// потому, что активен мини-плеер).</summary>
    public bool IsAlwaysOnTop =>
        AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true };

    /// <summary>
    /// «Поверх всех окон» без смены размера и панелей — в отличие от
    /// мини-плеера. В полноэкранном режиме смысла не имеет (окно и так
    /// поверх всего), поэтому там игнорируется. Состояние сессионное,
    /// в настройки не сохраняется.
    /// </summary>
    public void SetAlwaysOnTop(bool enable)
    {
        if (IsOsFullScreen || _miniPlayer)
        {
            return;
        }

        _alwaysOnTop = enable;
        (AppWindow.Presenter as OverlappedPresenter)!.IsAlwaysOnTop = enable;
    }

    /// <summary>
    /// Мини-плеер: компактное окно 480x270 поверх всех окон, без панелей
    /// (они скрывает MainPage). Повторный вызов возвращает обычный режим.
    /// </summary>
    public void ToggleMiniPlayer()
    {
        if (IsOsFullScreen)
        {
            SetOsFullScreen(false);
        }

        if (!_miniPlayer)
        {
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            _preMiniPlacement = new Windows.Graphics.RectInt32(pos.X, pos.Y, size.Width, size.Height);

            _miniPlayer = true;
            _alwaysOnTopBeforeMini = _alwaysOnTop;
            (AppWindow.Presenter as OverlappedPresenter)!.IsAlwaysOnTop = true;

            AppWindow.Resize(new Windows.Graphics.SizeInt32(480, 300));
        }
        else
        {
            _miniPlayer = false;


            (AppWindow.Presenter as OverlappedPresenter)!.IsAlwaysOnTop = _alwaysOnTopBeforeMini;
            AppWindow.MoveAndResize(_preMiniPlacement);
        }
    }

    /// <summary>Левый клик по иконке в трее / пункт «Показать».</summary>
    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
        (AppWindow.Presenter as OverlappedPresenter)?.Restore();
        App.Tray?.Hide();
    }

    /// <summary>Пункт «Выход» в трее — настоящее закрытие окна.</summary>
    public void ExitFromTray()
    {
        App.AllowClose = true;
        Close();
    }

    public void SetOsFullScreen(bool enable)
    {
        AppWindow.SetPresenter(enable
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Default);

        AppTitleBar.Visibility = enable ? Visibility.Collapsed : Visibility.Visible;
        TitleBarRowDefinition.Height = enable ? new GridLength(0) : GridLength.Auto;

        if (!enable && _alwaysOnTop)
        {
            (AppWindow.Presenter as OverlappedPresenter)!.IsAlwaysOnTop = true;
        }
    }

    /// <summary>
    /// Восстанавливает сохранённые позицию/размер окна (SettingsService —
    /// синхронный файловый ввод-вывод, поэтому вызов до Activate() не блокирует
    /// запуск и не блокирует поток). Координаты вписываются в рабочую область
    /// ближайшего монитора: если окно сохранено на отключённом мониторе, оно
    /// не окажется за экраном. Развёрнутое окно просто максимизируется.
    /// </summary>
    public void RestorePlacement()
    {
        try
        {

            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            var settings = settingsService.LoadAsync().GetAwaiter().GetResult();
            var saved = settings.WindowPlacement;
            if (saved == null || saved.Width < 200 || saved.Height < 200)
            {
                return;
            }

            if (saved.Maximized)
            {
                (AppWindow.Presenter as OverlappedPresenter)?.Maximize();
                return;
            }

            var area = DisplayArea.GetFromPoint(
                new PointInt32(saved.Left, saved.Top), DisplayAreaFallback.Primary);
            var work = area.WorkArea;

            var width = Math.Min(saved.Width, work.Width);
            var height = Math.Min(saved.Height, work.Height);
            var left = Math.Clamp(saved.Left, work.X, work.X + work.Width - width);
            var top = Math.Clamp(saved.Top, work.Y, work.Y + work.Height - height);

            AppWindow.MoveAndResize(new RectInt32(left, top, width, height));
        }
        catch
        {


        }
    }

    /// <summary>
    /// Текущее состояние окна для сохранения в настройках. null — если окно
    /// свёрнуто (координаты свёрнутого окна недействительны).
    /// </summary>
    public WindowPlacement? CapturePlacement()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            {
                return null;
            }

            var position = AppWindow.Position;
            var size = AppWindow.Size;
            return new WindowPlacement
            {
                Left = position.X,
                Top = position.Y,
                Width = size.Width,
                Height = size.Height,
                Maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized }
            };
        }
        catch
        {
            return null;
        }
    }
}
