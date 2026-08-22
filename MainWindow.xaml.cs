using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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
                return;
            }

            App.Tray?.Dispose();
            App.Tray = null;
        };

        // Иконка создаётся один раз на сессию: клик — показать, правый клик — меню.
        App.Tray ??= new Services.TrayIconService(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            ShowFromTray,
            ExitFromTray);

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    private bool _miniPlayer;
    private Windows.Graphics.RectInt32 _preMiniPlacement;

    /// <summary>Активен ли компактный режим мини-плеера (always-on-top).</summary>
    public bool IsMiniPlayer => _miniPlayer;

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
            (AppWindow.Presenter as OverlappedPresenter)!.IsAlwaysOnTop = true;
            // 16:9 + запас на рамку и строку заголовка.
            AppWindow.Resize(new Windows.Graphics.SizeInt32(480, 300));
        }
        else
        {
            _miniPlayer = false;
            (AppWindow.Presenter as OverlappedPresenter)!.IsAlwaysOnTop = false;
            AppWindow.MoveAndResize(_preMiniPlacement);
        }
    }

    /// <summary>Левый клик по иконке в трее / пункт «Показать».</summary>
    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
        (AppWindow.Presenter as OverlappedPresenter)?.Restore();
    }

    /// <summary>Пункт «Выход» в трее — настоящее закрытие окна.</summary>
    public void ExitFromTray()
    {
        App.AllowClose = true;
        Close();
    }

    /// <summary>
    /// Включает/выключает настоящий полноэкранный режим уровня ОС через
    /// AppWindow presenter. В отличие от простого разворачивания панелей
    /// внутри страницы, это убирает рамку и заголовок окна и разворачивает
    /// его на весь экран средствами Windows (AppWindowPresenterKind.FullScreen).
    ///
    /// Важно: AppWindowPresenterKind.FullScreen убирает только системную
    /// рамку/заголовок ОС. Наш собственный элемент TitleBar (AppTitleBar) —
    /// это часть контента страницы в отдельной строке Grid'а, presenter его
    /// не трогает, поэтому его нужно скрывать вручную, иначе сверху экрана
    /// останется полоса и контент не займёт весь монитор.
    /// </summary>
    public void SetOsFullScreen(bool enable)
    {
        AppWindow.SetPresenter(enable
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Default);

        AppTitleBar.Visibility = enable ? Visibility.Collapsed : Visibility.Visible;
        TitleBarRowDefinition.Height = enable ? new GridLength(0) : GridLength.Auto;
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
