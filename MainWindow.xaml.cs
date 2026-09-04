using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using IptvPlayer.Models;
using IptvPlayer.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace IptvPlayer;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
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

        // Крестик сворачивает в трей (продолжая играть звук) — реальный
        // выход через меню иконки в трее. AppWindow.Closing — единственная
        // точка, где закрытие можно отменить.
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
                closeToTray = false; // настройки не прочитались — выходим честно.
            }

            if (!App.AllowClose && closeToTray)
            {
                e.Cancel = true;
                AppWindow.Hide();
                App.Tray?.Show(); // иконка в трее живёт только пока окно скрыто
                return;
            }

            // Настоящий выход: если пользователь отложил установку обновления
            // («Позже»), запускаем установщик — приложение сейчас закроется и
            // освободит файлы для копирования.
            App.TryStartPendingUpdateInstall();

            MinimizeHook?.Dispose();
            App.Tray?.Dispose();
            App.Tray = null;
        };

        // «Свернуть» прячет окно в трей (по настройке) — тогда в панели задач
        // его нет, а иконка в трее, наоборот, появляется. OverlappedPresenter
        // в этой версии Windows App SDK не имеет события состояния, поэтому
        // перехватываем WM_SIZE через subclass оконной процедуры.
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
                minimizeToTray = false; // настройки не прочитались — обычное сворачивание.
            }

            if (minimizeToTray)
            {
                AppWindow.Hide();
                App.Tray?.Show();
            }
        });

        // Иконка создаётся один раз на сессию: клик — показать, правый клик — меню.
        App.Tray ??= new Services.TrayIconService(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            ShowFromTray,
            ExitFromTray);

        // Навигация перенесена в App.OnLaunched (HubPage или MainPage)
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
            return; // в этих режимах окно уже поверх всего — не спорим с ними
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
            // 16:9 + запас на рамку и строку заголовка.
            AppWindow.Resize(new Windows.Graphics.SizeInt32(480, 300));
        }
        else
        {
            _miniPlayer = false;
            // Если «поверх всех окон» было включено до входа в мини-плеер,
            // окно остаётся поверх всех и после выхода из него.
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
        App.Tray?.Hide(); // окно снова видно — иконка в трее не нужна
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

        // Смена presenter'а создаёт новый OverlappedPresenter с настройками
        // по умолчанию — возвращаем включённый режим «поверх всех окон».
        if (!enable && _alwaysOnTop)
        {
            (AppWindow.Presenter as OverlappedPresenter)!.IsAlwaysOnTop = true;
        }
    }

    /// <summary>
    /// Восстанавливает сохранённые позицию/размер окна (SettingsService —
    /// синхронный файловый ввод-вывод, поэтому вызов до Activate() не тормозит
    /// запуск и не блокирует поток). Координаты вписываются в рабочую область
    /// ближайшего монитора: если окно сохранено на отключённом мониторе, оно
    /// не окажется за экраном. Развёрнутое окно просто максимизируется.
    /// </summary>
    public void RestorePlacement()
    {
        try
        {
            // Тот же singleton ISettingsService из DI-контейнера App, что и
            // везде в приложении (раньше здесь создавался одноразовый
            // new SettingsService()).
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
            // Битые/отсутствующие данные размещения — окно остаётся
            // с размерами по умолчанию, это не должно ронять запуск.
        }
    }

    /// <summary>
    /// Текущее состояние окна для сохранения в настройках. null — если окно
    /// свёрнуто (координаты свёрнутого окна бессмысленны).
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
