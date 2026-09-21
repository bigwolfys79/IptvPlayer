using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.UI;
using Windows.System;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IptvPlayer;

public sealed partial class HubPage : Page
{
    private readonly ISettingsService _settingsService;
    private readonly VodResumeStore _vodResumeStore;
    private AppSettings? _settings;
    private PlaylistSource? _lastWatchedPlaylist;
    private string? _lastWatchedChannelName;
    private List<(string Title, string RawTitle, int EpisodeIndex, VodResumePosition Position, int? PlaylistId, string? LocalPath)> _vodResumeItems = new();


    private const string LocalFileKeyPrefix = "file::";
    private FlyoutType? _openFlyout;
    private DispatcherTimer? _clockTimer;
    private bool _initialized;
    private bool _plaqueIsVod;


    private static bool _introPlayed;

    public HubPage()
    {
        InitializeComponent();
        ApplyXamlLocalization();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _vodResumeStore = App.Services.GetRequiredService<VodResumeStore>();
        Loaded += HubPage_Loaded;
        Unloaded += HubPage_Unloaded;

        ProcessKeyboardAccelerators += HubPage_ProcessKeyboardAccelerators;


        RootGrid.SizeChanged += (_, e) => ScheduleHubScaleUpdate();
        MainPanel.SizeChanged += (_, e) => ScheduleHubScaleUpdate();
    }

    private bool _hubScaleScheduled;

    // Deferred hub scale update
    private void ScheduleHubScaleUpdate()
    {


        if (_hubScaleScheduled)
        {
            return;
        }
        _hubScaleScheduled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _hubScaleScheduled = false;
            try
            {
                UpdateHubScale();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Масштабирование HubPage.");
            }
        });
    }

    // Scale hub panel to fit window width
    private void UpdateHubScale()
    {


        MainPanel.Measure(new Windows.Foundation.Size(
            double.PositiveInfinity, double.PositiveInfinity));
        var desiredWidth = MainPanel.DesiredSize.Width;
        if (RootGrid.ActualWidth <= 0 || desiredWidth <= 0)
        {
            return;
        }
        MainPanel.InvalidateMeasure();

        var target = Math.Min(1.0, (RootGrid.ActualWidth - 32) / desiredWidth);
        var clamped = Math.Max(0.4, target);
        MainPanelScale.ScaleX = clamped;
        MainPanelScale.ScaleY = clamped;

        if (Math.Abs(clamped - _lastHubScale) > 0.005)
        {
            _lastHubScale = clamped;
            Serilog.Log.Information(
                "HubScale: root={Root:F0} desired={Desired:F0} actual={Actual:F0} target={Target:F2} scale={Scale:F2}",
                RootGrid.ActualWidth, desiredWidth, MainPanel.ActualWidth, target, clamped);
        }
    }

    private double _lastHubScale = -1;

    private void HubPage_ProcessKeyboardAccelerators(UIElement sender, ProcessKeyboardAcceleratorEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            CloseFlyout();
            args.Handled = true;
        }
    }

    private async void HubPage_Loaded(object sender, RoutedEventArgs e)
    {
        // Resubscribe on every Loaded — Unloaded removes the handler
        if (MainWindow.Instance != null)
        {
            MainWindow.Instance.Activated += MainWindow_Activated;
        }

        if (_initialized)
        {
            UpdateLocalizedTexts();
            SetGreeting(welcomeBack: true);
            _clockTimer?.Start();
            return;
        }
        _initialized = true;

        Serilog.Log.Information("HubPage_Loaded: старт");
        try
        {
            _settings = await _settingsService.LoadAsync();
            // Keep VM settings real even if MainPage never opens (close-time save)
            App.Services.GetRequiredService<ViewModels.MainPageViewModel>().AppSettings = _settings;

            if (_settings.Playlists.Count == 0)
            {
                await ShowWelcomeDialogAsync();
                _settings = await _settingsService.LoadAsync();
                if (_settings.Playlists.Count == 0)
                {
                    Serilog.Log.Information("HubPage_Loaded: плейлистов нет и после приветствия → MainPage");
                    Frame.Navigate(typeof(MainPage));
                    return;
                }
            }

            var allPositions = await _vodResumeStore.LoadAllAsync();
            var now = DateTime.Now;
            _vodResumeItems = allPositions
                .Where(p => p.Value.PositionSeconds >= 30 &&
                            (p.Value.DurationSeconds <= 0 ||
                             p.Value.PositionSeconds <= p.Value.DurationSeconds * 0.95) &&
                            (now - p.Value.UpdatedAt).TotalDays < 30)
                .OrderByDescending(p => p.Value.UpdatedAt)
                .Take(5)
                .Select(p =>
                {
                    var key = p.Key;
                    var episodeIndex = p.Value.EpisodeIndex;

                    if (key.StartsWith(LocalFileKeyPrefix, StringComparison.Ordinal))
                    {
                        var path = key[LocalFileKeyPrefix.Length..];
                        return (Path.GetFileNameWithoutExtension(path), path, -1, p.Value, (int?)null, path);
                    }

                    var parts = key.Split("::");
                    var title = parts[0];
                    var display = episodeIndex >= 0
                        ? string.Format(L.T("Seriya_Nomer_0"), title, episodeIndex + 1)
                        : title;
                    return (display, title, episodeIndex, p.Value, p.Value.PortalPlaylistId, (string?)null);
                })
                .ToList();

            RefreshDerived();

            UpdateLocalizedTexts();
            SetGreeting(welcomeBack: _introPlayed);
            SetupClockTimer();
            SetupFlyoutShadow();
            await AnimateInAsync();

            Serilog.Log.Information("HubPage_Loaded: завершено (Live={Live}, VOD={VOD})",
                _lastWatchedPlaylist != null, _vodResumeItems.Count);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "HubPage_Loaded: ошибка");
        }
    }


    private void RefreshDerived()
    {
        if (_settings == null) return;

        _lastWatchedPlaylist =
            _settings.Playlists.FirstOrDefault(p => !p.IsPortal && p.Id == _settings.ActivePlaylistId && !string.IsNullOrEmpty(p.LastWatchedChannel)) ??
            _settings.Playlists.FirstOrDefault(p => !p.IsPortal && !string.IsNullOrEmpty(p.LastWatchedChannel));
        _lastWatchedChannelName = _lastWatchedPlaylist?.LastWatchedChannel;

        UpdateContinueButton();
    }


    private void UpdateContinueButton()
    {
        if (_vodResumeItems.Count > 0)
        {
            _plaqueIsVod = true;
            ContinueText.Text = string.Format(L.T("Prodolzhit_0"), _vodResumeItems[0].Title);
            ContinueButton.Visibility = Visibility.Visible;
        }
        else if (_lastWatchedPlaylist != null && !string.IsNullOrEmpty(_lastWatchedChannelName))
        {
            _plaqueIsVod = false;
            ContinueText.Text = string.Format(L.T("Vklyuchit_0"), _lastWatchedChannelName);
            ContinueButton.Visibility = Visibility.Visible;
        }
        else
        {
            ContinueButton.Visibility = Visibility.Collapsed;
        }
    }


    private void UpdateLocalizedTexts()
    {
        ToolTipService.SetToolTip(PlaylistsButton, $"{L.T("Hub_Pleylisty_ToolTip")} — 1");
        ToolTipService.SetToolTip(PortalButton, $"{L.T("Portal_Lbl")} — 2");
        ToolTipService.SetToolTip(VideoButton, $"{L.T("Hub_Video_ToolTip")} — 3");
        ToolTipService.SetToolTip(SettingsButton, $"{L.T("Nastroyki_Card_Lbl")} — 4");
        ToolTipService.SetToolTip(InfoButton, $"{L.T("Hotkeys_Title")} — F1");
        UpdateContinueButton();
    }

    private void HubPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_clockTimer != null)
            _clockTimer.Stop();
        if (MainWindow.Instance != null)
            MainWindow.Instance.Activated -= MainWindow_Activated;
    }

    private void SetGreeting(bool welcomeBack = false)
    {
        if (welcomeBack)
        {
            GreetingText.Text = L.T("S_vozvrashcheniem");
        }
        else
        {
            var hour = DateTime.Now.Hour;
            GreetingText.Text = hour switch
            {
                >= 5 and < 12 => L.T("Dobroe_Utro"),
                >= 12 and < 18 => L.T("Dobryy_Den"),
                >= 18 and < 23 => L.T("Dobryy_Vecher"),
                _ => L.T("Dobroy_Nochi")
            };
        }
        SubText.Text = L.T("IptvPlayer_Vyberite_Chto");
    }


    private void SetupClockTimer()
    {
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _clockTimer.Tick += (_, _) => SetGreeting();
        _clockTimer.Start();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
            CloseFlyout();
    }

    private async System.Threading.Tasks.Task AnimateInAsync()
    {
        MainPanel.Opacity = 0;
        MainPanel.Visibility = Visibility.Visible;


        if (_introPlayed)
        {
            AccentLineTransform.ScaleX = 1;
            ResetCardTransform(PlaylistsTransform);
            ResetCardTransform(PortalTransform);
            ResetCardTransform(SettingsTransform);
            ResetCardTransform(VideoTransform);
            MainPanel.Opacity = 1;
            _introPlayed = true;
            return;
        }

        ApplyGreetingShadow();


        await RunStoryboard(new DoubleAnimation
        {
            From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, MainPanel, "Opacity");


        var sb = new Storyboard();
        AddAnimation(sb, AccentLineTransform, "ScaleX", 0, 1, 400, 250);
        AddCardAnimation(sb, PlaylistsTransform, -180, 0);
        AddCardAnimation(sb, PortalTransform, 180, 80);
        AddCardAnimation(sb, VideoTransform, -180, 80);
        AddCardAnimation(sb, SettingsTransform, 180, 120);
        await RunStoryboard(sb);


        if (ContinueButton.Visibility == Visibility.Visible)
        {
            var fade = new Storyboard();
            AddAnimation(fade, ContinueButton, "Opacity", 0, 1, 300, 0);
            await RunStoryboard(fade);
        }

        _introPlayed = true;
    }

    private static void ResetCardTransform(CompositeTransform transform)
    {
        transform.Rotation = 0;
        transform.ScaleX = 1;
        transform.ScaleY = 1;
    }

    private static void AddCardAnimation(Storyboard sb, CompositeTransform transform, double fromRotation, int delayMs)
    {
        AddAnimation(sb, transform, "Rotation", fromRotation, 0, 350, delayMs);
        AddAnimation(sb, transform, "ScaleX", 0.3, 1, 350, delayMs);
        AddAnimation(sb, transform, "ScaleY", 0.3, 1, 350, delayMs);
    }

    private static void AddAnimation(Storyboard sb, DependencyObject target, string property,
        double from, double to, int durationMs, int delayMs)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        sb.Children.Add(anim);
    }

    private static System.Threading.Tasks.Task RunStoryboard(Storyboard sb)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource();
        sb.Completed += (_, _) => tcs.TrySetResult();
        sb.Begin();
        return tcs.Task;
    }


    private static System.Threading.Tasks.Task RunStoryboard(DoubleAnimation anim,
        DependencyObject target, string property)
    {
        var sb = new Storyboard();
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        sb.Children.Add(anim);
        return RunStoryboard(sb);
    }

    private void ApplyGreetingShadow()
    {
        var visual = ElementCompositionPreview.GetElementVisual(GreetingText);
        var compositor = visual.Compositor;
        var shadow = compositor.CreateDropShadow();
        shadow.Offset = new System.Numerics.Vector3(0, 2, 0);
        shadow.BlurRadius = 24;
        shadow.Opacity = 0.6f;
        shadow.Color = Color.FromArgb(255, 255, 108, 160);

        var shadowVisual = compositor.CreateSpriteVisual();
        shadowVisual.Shadow = shadow;
        shadowVisual.Size = new System.Numerics.Vector2((float)GreetingText.ActualWidth, (float)GreetingText.ActualHeight);
        ElementCompositionPreview.SetElementChildVisual(GreetingText, shadowVisual);
    }

    private void SetupFlyoutShadow()
    {

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(FlyoutBorder);
            var compositor = visual.Compositor;
            var dropShadow = compositor.CreateDropShadow();
            dropShadow.Offset = new System.Numerics.Vector3(0, 8, 0);
            dropShadow.BlurRadius = 32;
            dropShadow.Opacity = 0.5f;
            dropShadow.Color = Windows.UI.Color.FromArgb(255, 0, 0, 0);

            var shadowVisual = compositor.CreateSpriteVisual();
            shadowVisual.Shadow = dropShadow;
            shadowVisual.Size = new System.Numerics.Vector2((float)FlyoutBorder.ActualWidth, (float)FlyoutBorder.ActualHeight);
            ElementCompositionPreview.SetElementChildVisual(FlyoutBorder, shadowVisual);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "HubPage: Composition DropShadow не настроен, тень отключена");
        }
    }

    private async System.Threading.Tasks.Task ShowWelcomeDialogAsync()
    {
        var dialog = new ThemedContentDialog
        {
            Title = L.T("Welcome_Title"),
            Content = new TextBlock
            {
                Text = L.T("Welcome_Text"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14
            },
            PrimaryButtonText = L.T("Welcome_AddPlaylist"),
            SecondaryButtonText = L.T("Welcome_AddPortal"),
            CloseButtonText = L.T("Welcome_Later"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        var choice = await DialogQueue.ShowAsync(dialog);

        if (choice == ContentDialogResult.Primary || choice == ContentDialogResult.Secondary)
            await OpenPlaylistSettingsAsync();
    }

    private async System.Threading.Tasks.Task OpenPlaylistSettingsAsync()
    {
        var viewModel = App.Services.GetRequiredService<MainPageViewModel>();

        viewModel.AppSettings = await _settingsService.LoadAsync();
        var m3uParser = App.Services.GetRequiredService<IM3UParserService>();
        var channelRepo = App.Services.GetRequiredService<IChannelRepository>();
        var cacheService = App.Services.GetRequiredService<IPlaylistCacheService>();
        var logger = App.Services.GetRequiredService<ILogger<Dialogs.PlaylistSettingsDialog>>();
        var xmlTv = App.Services.GetRequiredService<IXmlTvService>();
        var d = new Dialogs.PlaylistSettingsDialog(viewModel, _settingsService, m3uParser, channelRepo, cacheService, logger, _ => System.Threading.Tasks.Task.CompletedTask, xmlTv);
        await d.ShowAsync(Content.XamlRoot);

        _settings = await _settingsService.LoadAsync();
        RefreshDerived();
    }

    private enum FlyoutType { Playlists, Portal, Settings }

    // Open hub flyout menu
    private void ShowCustomFlyout(FlyoutType type, FrameworkElement anchor)
    {


        if (_openFlyout == type)
        {
            CloseFlyout();
            return;
        }
        _openFlyout = type;

        FlyoutContent.Children.Clear();

        if (type == FlyoutType.Playlists)
            BuildPlaylistsFlyoutContent();
        else if (type == FlyoutType.Portal)
            BuildPortalFlyoutContent();
        else
            BuildSettingsFlyoutContent();

        var windowSize = XamlRoot != null
            ? new Windows.Foundation.Size(XamlRoot.Size.Width, XamlRoot.Size.Height)
            : new Windows.Foundation.Size(1280, 800);


        FlyoutScroll.MaxHeight = Math.Max(200, windowSize.Height - 40);


        var pad = FlyoutBorder.Padding;
        var frame = FlyoutBorder.BorderThickness;
        FlyoutContent.Measure(new Windows.Foundation.Size(
            Math.Max(0, windowSize.Width - 32 - pad.Left - pad.Right - frame.Left - frame.Right),
            Math.Max(0, windowSize.Height - 32 - pad.Top - pad.Bottom - frame.Top - frame.Bottom)));
        double flyoutW = FlyoutContent.DesiredSize.Width + pad.Left + pad.Right + frame.Left + frame.Right;
        double flyoutH = Math.Min(
            FlyoutContent.DesiredSize.Height + pad.Top + pad.Bottom + frame.Top + frame.Bottom,
            FlyoutScroll.MaxHeight);


        var scale = MainPanelScale.ScaleX;
        var layoutPos = anchor.TransformToVisual(this)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        var panelLayoutPos = MainPanel.TransformToVisual(this)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        var origin = new Windows.Foundation.Point(
            panelLayoutPos.X + MainPanel.ActualWidth / 2,
            panelLayoutPos.Y + MainPanel.ActualHeight / 2);
        var visualPos = new Windows.Foundation.Point(
            origin.X + (layoutPos.X - origin.X) * scale,
            origin.Y + (layoutPos.Y - origin.Y) * scale);
        var visualW = anchor.ActualWidth * scale;
        var visualH = anchor.ActualHeight * scale;


        double left = visualPos.X;
        double top = visualPos.Y + visualH + 8;

        if (top + flyoutH > windowSize.Height - 16)
        {
            top = visualPos.Y - flyoutH - 8;
        }


        top = Math.Min(Math.Max(16, top), Math.Max(16, windowSize.Height - flyoutH - 16));
        left = Math.Min(Math.Max(16, left), Math.Max(16, windowSize.Width - flyoutW - 16));
        Serilog.Log.Information(
            "HubFlyout: win={W}x{H} visual={VX:F0},{VY:F0} flyout={FW:F0}x{FH:F0} left={L:F0} top={T:F0}",
            windowSize.Width, windowSize.Height, visualPos.X, visualPos.Y, flyoutW, flyoutH, left, top);

        FlyoutBorder.Margin = new Thickness(left, top, 0, 0);
        FlyoutBorder.Visibility = Visibility.Visible;
        FlyoutOverlay.Visibility = Visibility.Visible;

        var sb = new Storyboard();
        AddAnimation(sb, FlyoutBorder, "Opacity", 0, 1, 150, 0);
        AddAnimation(sb, FlyoutTransform, "ScaleX", 0.8, 1, 150, 0);
        AddAnimation(sb, FlyoutTransform, "ScaleY", 0.8, 1, 150, 0);
        sb.Begin();
    }

    private void FlyoutOverlay_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        CloseFlyout();
        e.Handled = true;
    }

    // Close hub flyout menu
    private void CloseFlyout()
    {
        _openFlyout = null;
        FlyoutBorder.Visibility = Visibility.Collapsed;
        FlyoutOverlay.Visibility = Visibility.Collapsed;
    }

    private void BuildPlaylistsFlyoutContent()
    {
        var m3us = _settings?.Playlists.Where(p => !p.IsPortal).ToList() ?? new();

        if (m3us.Count == 0)
        {
            AddFlyoutItem("\uE783", L.T("Net_Pleylistov_Dobavte"), "#8CFFFFFF", null, false);
            return;
        }

        if (m3us.Count == 1)
        {
            AddFlyoutItem("\uE8AB", L.T("Zagruzit_Pleylist"), null, (_, _) =>
                Frame.Navigate(typeof(MainPage), (m3us[0], skipResume: true)));
        }
        else
        {

            foreach (var playlist in m3us)
            {
                var captured = playlist;
                var isActive = _settings?.ActivePlaylistId == captured.Id;
                AddFlyoutItem(
                    isActive ? "\uE73E" : "\uE8AB",
                    captured.Name,
                    null,
                    (_, _) => Frame.Navigate(typeof(MainPage), (captured, skipResume: true)));
            }
        }

        if (_lastWatchedPlaylist != null && !string.IsNullOrEmpty(_lastWatchedChannelName))
        {
            AddFlyoutSeparator();
            AddFlyoutItem("\uE7F4", string.Format(L.T("Posledniy_Kanal_0"), _lastWatchedChannelName), "#99FFFFFF", (_, _) => NavigateToLastChannel());
        }
    }

    private void BuildPortalFlyoutContent()
    {
        var portals = _settings?.Playlists.Where(p => p.IsPortal).ToList() ?? new();

        if (portals.Count == 0)
        {
            AddFlyoutItem("\uE783", L.T("Net_Portala_Dobavte"), "#8CFFFFFF", null, false);
            return;
        }

        if (portals.Count == 1)
        {
            AddFlyoutItem("\uE774", L.T("Zagruzit_Portal"), null, (_, _) =>
                Frame.Navigate(typeof(MainPage), (portals[0], skipResume: true)));
        }
        else
        {
            foreach (var portal in portals)
            {
                var captured = portal;
                var isActive = _settings?.ActivePlaylistId == captured.Id;
                AddFlyoutItem(
                    isActive ? "\uE73E" : "\uE774",
                    captured.Name,
                    null,
                    (_, _) => Frame.Navigate(typeof(MainPage), (captured, skipResume: true)));
            }
        }

        var portalResumeItems = _vodResumeItems.Where(i => i.LocalPath == null).ToList();
        if (portalResumeItems.Count > 0)
        {
            AddFlyoutSeparator();
            AddFlyoutItem("\uE8B6", string.Format(L.T("Nedosmotrennye_0"), portalResumeItems.Count), "#99FFFFFF", null, false);

            foreach (var item in portalResumeItems)
            {
                var captured = item;
                AddFlyoutItem("\uE8B6", captured.Title, null, (_, _) => VodMenuItem_Click(captured),
                    subtitle: FormatRemaining(captured.Position));
            }
        }
    }


    private static string? FormatRemaining(VodResumePosition position)
    {
        if (position.DurationSeconds <= 0)
            return null;
        var minutes = (int)Math.Ceiling((position.DurationSeconds - position.PositionSeconds) / 60.0);
        return minutes > 0 ? string.Format(L.T("Ostalos_Min_0"), minutes) : null;
    }

    private void BuildSettingsFlyoutContent()
    {
        AddFlyoutItem("\uE771", L.T("Pleylisty_Lbl"), null, async (_, _) =>
        {
            CloseFlyout();
            await OpenPlaylistSettingsAsync();
        });
        AddFlyoutItem("\uE770", L.T("Interfeys_Lbl"), null, async (_, _) =>
        {
            CloseFlyout();
            var viewModel = App.Services.GetRequiredService<MainPageViewModel>();


            viewModel.AppSettings = await _settingsService.LoadAsync();
            var d = new Dialogs.InterfaceSettingsDialog(viewModel, _settingsService, App.ApplyAppTheme);
            await d.ShowAsync(Content.XamlRoot);

            UpdateLocalizedTexts();
        });
        AddFlyoutItem("\uE769", L.T("Vosproizvedenie_Lbl"), null, async (_, _) =>
        {
            CloseFlyout();
            var viewModel = App.Services.GetRequiredService<MainPageViewModel>();


            viewModel.AppSettings = await _settingsService.LoadAsync();
            var streamService = App.Services.GetRequiredService<IStreamService>();
            var d = new Dialogs.PlaybackSettingsDialog(viewModel, _settingsService, streamService);
            await d.ShowAsync(Content.XamlRoot);
        });


        AddFlyoutItem("\uEC4B", L.T("License_Dialog_Title"), null, async (_, _) =>
        {
            CloseFlyout();
            var d = new Dialogs.LicenseStatusDialog();
            await d.ShowAsync(Content.XamlRoot);
        });
        AddFlyoutItem("\uE946", L.T("O_Programme_Lbl"), null, async (_, _) =>
        {
            CloseFlyout();
            var updateService = App.Services.GetRequiredService<IUpdateService>();
            var d = new Dialogs.AboutDialog(updateService, OfferUpdateInstallFromHubAsync);
            await d.ShowAsync(Content.XamlRoot);
        });
    }


    private async Task OfferUpdateInstallFromHubAsync(Version version, string setupPath)
    {
        var dialog = new ThemedContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = L.T("Dostupno_Obnovlenie"),
            Content = string.Format(L.T("Versiya_0_Skachana_Ustanovit_Seychas_Prilozhenie"), version, version),
            PrimaryButtonText = L.T("Ustanovit_Seychas"),
            CloseButtonText = L.T("Pozzhe"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await DialogQueue.ShowAsync(dialog) != ContentDialogResult.Primary)
        {

            App.PendingUpdateSetupPath = setupPath;
            return;
        }

        if (App.Services.GetRequiredService<RecordingService>().IsActive)
        {
            var info = new ThemedContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = L.T("Obnovlenie_Otlozheno"),
                Content = L.T("Idet_Zapis_Peredach_Obnovlenie_Ustanovitsya_Avtomaticheski"),
                CloseButtonText = L.T("Ponyatno")
            };
            await DialogQueue.ShowAsync(info);
            return;
        }

        App.Services.GetRequiredService<IUpdateService>().RunInstallerAndExit(setupPath);
    }


    private static bool HubIsLight =>
        (MainWindow.Instance?.Content as FrameworkElement)?.ActualTheme == ElementTheme.Light;

    private static Microsoft.UI.Xaml.Media.SolidColorBrush HubFg(byte alpha) =>
        new(HubIsLight ? Color.FromArgb(alpha, 0, 0, 0) : Color.FromArgb(alpha, 255, 255, 255));

    private void AddFlyoutItem(string glyph, string text, string? foreground, RoutedEventHandler? click,
        bool enabled = true, string? subtitle = null)
    {
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            Background = HubFg(20),
            IsEnabled = enabled
        };

        if (click != null) btn.Click += click;

        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        sp.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = foreground != null
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(HubIsLight
                    ? Color.FromArgb(255, 0, 95, 184)
                    : Color.FromArgb(255, 153, 255, 255))
                : HubFg(255),
            VerticalAlignment = VerticalAlignment.Center
        });

        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 14,
            Foreground = foreground != null
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(HubIsLight
                    ? Color.FromArgb(180, 0, 95, 184)
                    : Color.FromArgb(180, 153, 255, 255))
                : HubFg(255),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 240
        };

        if (subtitle != null)
        {
            var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textPanel.Children.Add(textBlock);
            textPanel.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = HubFg(170),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 240
            });
            sp.Children.Add(textPanel);
        }
        else
        {
            sp.Children.Add(textBlock);
        }

        btn.Content = sp;
        FlyoutContent.Children.Add(btn);
    }

    private void AddFlyoutSeparator()
    {
        FlyoutContent.Children.Add(new Border
        {
            Height = 1,
            Background = HubFg(40),
            Margin = new Thickness(8, 4, 8, 4)
        });
    }

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ShowHotkeysDialogAsync();
    }

    private void InfoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = ShowHotkeysDialogAsync();
    }


    private async System.Threading.Tasks.Task ShowHotkeysDialogAsync()
    {
        var rows = new (string Key, string DescKey)[]
        {
            ("0…9", "HK_Digits"),
            ("Enter", "HK_Enter"),
            ("↑ ↓ PgUp PgDn", "HK_Zap"),
            ("Backspace", "HK_Back"),
            ("Space", "HK_Space"),
            ("M", "HK_M"),
            ("V", "HK_V"),
            ("F / F11", "HK_F"),
            ("Esc", "HK_Esc"),
            ("Ctrl+F", "HK_CtrlF"),
            ("Ctrl+J", "HK_CtrlJ"),
            ("Ctrl+M", "HK_CtrlM"),
            ("Ctrl+T", "HK_CtrlT"),
            ("1 / 2 / 3 / 4", "HK_Hub123"),
            ("F1", "HK_F1"),
        };

        var panel = new StackPanel { Spacing = 10 };
        foreach (var (key, descKey) in rows)
        {
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var chip = new Border
            {
                Background = HubFg(30),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = key,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 255, 108, 160))
                }
            };
            Grid.SetColumn(chip, 0);
            row.Children.Add(chip);

            var desc = new TextBlock
            {
                Text = L.T(descKey),
                FontSize = 14,
                Foreground = HubFg(230),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(desc, 1);
            row.Children.Add(desc);

            panel.Children.Add(row);
        }

        var dialog = new ThemedContentDialog
        {
            Title = L.T("Hotkeys_Title"),
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 480,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            CloseButtonText = L.T("Zakryt"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        await DialogQueue.ShowAsync(dialog);
    }

    private void PlaylistsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowCustomFlyout(FlyoutType.Playlists, PlaylistsButton);
    }

    private void PortalButton_Click(object sender, RoutedEventArgs e)
    {
        ShowCustomFlyout(FlyoutType.Portal, PortalButton);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowCustomFlyout(FlyoutType.Settings, SettingsButton);
    }


    private async void VideoButton_Click(object sender, RoutedEventArgs e)
    {
        await PickAndPlayLocalVideoAsync();
    }

    private async System.Threading.Tasks.Task PickAndPlayLocalVideoAsync()
    {
        var pickerService = App.Services.GetRequiredService<LocalVideoFileService>();
        var file = await pickerService.PickAsync();
        if (file != null)
        {
            Frame.Navigate(typeof(MainPage), file);
        }
    }

    private void PlaylistsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ShowCustomFlyout(FlyoutType.Playlists, PlaylistsButton);
    }

    private void PortalAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ShowCustomFlyout(FlyoutType.Portal, PortalButton);
    }

    private void SettingsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ShowCustomFlyout(FlyoutType.Settings, SettingsButton);
    }

    private void VideoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = PickAndPlayLocalVideoAsync();
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_plaqueIsVod)
        {
            if (_vodResumeItems.Count > 0)
                VodMenuItem_Click(_vodResumeItems[0]);
        }
        else
        {
            CloseFlyout();
            NavigateToLastChannel();
        }
    }

    private void NavigateToLastChannel()
    {
        if (_lastWatchedPlaylist != null)
        {
            Frame.Navigate(typeof(MainPage), _lastWatchedPlaylist);
        }
    }

    private void VodMenuItem_Click(ValueTuple<string, string, int, VodResumePosition, int?, string?> item)
    {
        var (title, rawTitle, episodeIndex, position, playlistId, localPath) = item;


        if (localPath != null)
        {
            CloseFlyout();
            Frame.Navigate(typeof(MainPage), new LocalVideoFile(localPath, title));
            return;
        }

        var playlist = playlistId.HasValue
            ? _settings?.Playlists.FirstOrDefault(p => p.Id == playlistId.Value)
            : _settings?.Playlists.FirstOrDefault(p => p.IsPortal);

        if (playlist != null)
        {
            CloseFlyout();
            Frame.Navigate(typeof(MainPage), (playlist, rawTitle, episodeIndex));
        }
    }
}
