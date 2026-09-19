using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;

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
                closeToTray = SettingsService.Current?.CloseToTray ?? false;
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

            // Persist window position on real close (Closed may not save if exit is abrupt)
            try
            {
                var placement = CapturePlacement();
                if (placement != null && SettingsService.Current is { } settings)
                {
                    settings.WindowPlacement = placement;
                }
            }
            catch
            {
                // Placement save is best effort
            }

            App.TryStartPendingUpdateInstall();

            MinimizeHook?.Dispose();
            App.Tray?.Dispose();
            App.Tray = null;
        };

        Closed += OnWindowClosed;

        MinimizeHook = new MinimizeToTrayHook(this, () =>
        {
            bool minimizeToTray;
            try
            {
                minimizeToTray = SettingsService.Current?.MinimizeToTray ?? false;
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


    // Moved from MainPage ctor: save state and exit once, per window (not per page instance)
    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            var vm = App.Services.GetRequiredService<MainPageViewModel>();

            var placement = CapturePlacement();
            if (placement != null)
            {
                vm.AppSettings.WindowPlacement = placement;
            }
            vm.AppSettings.Volume = vm.Player.LastUserVolume ?? 1.0;

            if (MainPage.LastChannelListWidth is { } channelListWidth)
            {
                vm.AppSettings.ChannelListWidth = channelListWidth;
            }

            vm.AppSettings.InterruptedRecordings = vm.Recording.Active
                .Select(r => new Models.InterruptedRecording
                {
                    ChannelName = r.ChannelName,
                    ProgramName = r.ChannelName,
                    EndTime = r.DurationSec is > 0
                        ? r.StartedAt.AddSeconds(r.DurationSec.Value)
                        : null
                })
                .ToList();

            await App.Services.GetRequiredService<ISettingsService>().SaveAsync(vm.AppSettings);
            await vm.FlushVodResumePositionsAsync();

            vm.Recording.StopAll();

            vm.Player.Stop();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Ошибка при сохранении состояния перед выходом.");
        }

        App.Tray?.Dispose();
        App.Tray = null;
        Serilog.Log.CloseAndFlush();

        Environment.Exit(0);
    }


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
            var settings = SettingsService.Current;
            if (settings == null)
            {
                return;
            }
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
