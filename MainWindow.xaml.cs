using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using IptvPlayer.Models;
using IptvPlayer.Services;

namespace IptvPlayer;


public sealed partial class MainWindow : Window
{


    public static MainWindow? Instance { get; private set; }


    public Frame AppFrame => RootFrame;


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


    private bool _alwaysOnTop;


    private bool _alwaysOnTopBeforeMini;


    private Services.MinimizeToTrayHook? MinimizeHook;


    public bool IsMiniPlayer => _miniPlayer;


    public bool IsAlwaysOnTop =>
        AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true };


    public void SetAlwaysOnTop(bool enable)
    {
        if (IsOsFullScreen || _miniPlayer)
        {
            return;
        }

        _alwaysOnTop = enable;
        (AppWindow.Presenter as OverlappedPresenter)!.IsAlwaysOnTop = enable;
    }


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


    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
        (AppWindow.Presenter as OverlappedPresenter)?.Restore();
        App.Tray?.Hide();
    }


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
