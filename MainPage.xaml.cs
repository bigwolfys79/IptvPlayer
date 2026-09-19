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

namespace IptvPlayer;


public sealed partial class MainPage : Page
{
    private static string AllGroupsOption => L.T("Vse_Gruppy");

    private readonly IM3UParserService _m3uParserService;
    private readonly IVideoPortalService _videoPortalService;
    private readonly IUpdateService _updateService;
    private readonly ISettingsService _settingsService;
    private readonly IPlaylistCacheService _playlistCacheService;


    private PlaylistSource? _activePlaylist;

    private System.Threading.CancellationTokenSource? _playlistLoadCts;

    // Generation guard: only the newest playlist load may mutate shared state
    private int _playlistLoadGeneration;
    private readonly IStreamService _streamService;
    private readonly ChannelRepository _channelRepository;
    private readonly ILogger<MainPage> _logger;


    private readonly FrameServerRenderer _frameServerRenderer;

    private readonly EPGService _epgService;

    private PlayerViewModel Player => ViewModel.Player;

    private bool _isVolumeSliderSyncing;


    private PlaylistSource? _navigatedPlaylist;
    private bool _cameFromHub;
    private bool _skipResume;
    private string? _vodResumeChannelTitle;
    private int _vodResumeEpisodeIndex = -1;

    private bool _isFullScreen = false;
    private bool _wasEpgVisibleBeforeFullScreen = false;
    private double _channelListExpandedWidth = 320;

    private readonly DispatcherTimer _overlayHideTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private readonly DispatcherTimer _settingsSaveDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };


    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(30) };


    private bool _toastFailureLogged;

    private readonly DispatcherTimer _currentProgramRefreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    private readonly DispatcherTimer _archivePositionTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly DispatcherTimer _archiveSeekDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private Slider? _activeSeekSlider;
    private bool _updatingSeekBarValue;


    private readonly DispatcherTimer _volumeSaveDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    private Windows.Foundation.Point _lastWindowedOverlayPointerPosition = new(-1, -1);

    public MainPageViewModel ViewModel { get; }

    // Last known channel list width, read by MainWindow on close (window cannot access the page)
    internal static double? LastChannelListWidth;

    public MainPage()
    {

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

        Player.PlayerChanged += OnPlayerChangedApplyRenderer;
        Player.ArchiveStateChanged += OnPlayerArchiveStateChanged;

        Player.VodStateChanged += OnPlayerVodStateChanged;

        ViewModel.PortalEpisodePickRequested += OnPortalEpisodePickRequested;


        ViewModel.VodResumePromptRequested += OnVodResumePromptRequested;
        ViewModel.RecordingChanged += OnRecordingChangedUpdateButtons;


        ViewModel.ParentalUnlockRequested += OnParentalUnlockRequested;


        ViewModel.DailyLimitBlocked += OnDailyLimitBlocked;
        ViewModel.DailyLimitReached += OnDailyLimitReached;
        ViewModel.EpgVisibilityChanged += OnEpgVisibilityChanged;


        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.EpgViewModel.EpgReloaded += OnEpgReloaded;
        ViewModel.EpgViewModel.PropertyChanged += OnEpgViewModelPropertyChanged;
        Player.PropertyChanged += OnPlayerPropertyChangedBuffering;


        Player.PropertyChanged += OnPlayerPropertyChangedMuted;

        Player.PlayerChanged += OnPlayerChangedAttachBuffering;


        ViewModel.RecordingChanged += OnRecordingChangedShowError;
        ViewModel.FilterChanged += OnFilterChanged;
        ViewModel.ReminderToastRequested += OnReminderToastRequested;
        ViewModel.SettingsSaveRequested += OnSettingsSaveRequested;
        ViewModel.ScrollToProgramRequested += OnScrollToProgramRequested;
        ViewModel.ArchivePlayErrorRequested += OnArchivePlayErrorRequested;
        ViewModel.SleepTimerExpired += OnSleepTimerExpired;
        ViewModel.SleepTimerChanged += OnSleepTimerChanged;

        InitializeComponent();


        RootGrid.SizeChanged += OnRootLayoutSizeChanged;
        WindowedVideoOverlay.SizeChanged += OnRootLayoutSizeChanged;
        FullScreenBottomBar.SizeChanged += OnRootLayoutSizeChanged;

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

                _logger.LogError(ex, "InitializeAsync: исключение при старте страницы.");
            }
        };
        Unloaded += (s, e) =>
        {
            UnsubscribeViewModelEvents();
            _overlayHideTimer.Stop();
            _currentProgramRefreshTimer.Stop();
            _archivePositionTimer.Stop();
            _archiveSeekDebounceTimer.Stop();
            _reminderTimer.Stop();
            _settingsSaveDebounceTimer.Stop();
            _channelNumberInputTimer.Stop();
            StopPlayback();
            LastChannelListWidth = _isFullScreen
                ? _channelListExpandedWidth
                : Math.Max(0, ChannelListColumn.ActualWidth);
            ReleaseCursorHider();
            CancelPendingSeek();
            _frameServerRenderer.Detach();
            _frameServerRenderer.Dispose();
        };

        _currentProgramRefreshTimer.Tick += (s, e) =>
        {


            _ = ViewModel.EpgViewModel.RefreshCurrentProgramsLightAsync();
        };
        _currentProgramRefreshTimer.Start();

        _archivePositionTimer.Tick += (s, e) =>
        {
            Player.RefreshArchivePosition();
            UpdateArchiveSeekBar();
            Player.RefreshVodPosition();
            UpdateVodSeekBar();


            ViewModel.CaptureVodPosition();

            if (!_cursorHidden)
            {
                UpdateStatsOverlay();
            }
            ViewModel.CheckSleepTimer();
            UpdateSleepTimerDisplays();
            CheckDailyWatchLimit();
        };
        _archivePositionTimer.Start();


        _archiveSeekDebounceTimer.Tick += (s, e) => CommitArchiveSeek();

        Loaded += (s, e) =>
        {
            if (_hotkeysAttached || XamlRoot?.Content is not UIElement root)
            {
                return;
            }
            _hotkeysAttached = true;
            root.PreviewKeyDown += OnPagePreviewKeyDown;
        };


        _channelNumberInputTimer.Tick += (s, e) => CommitChannelNumber();


        UpdateMuteButtons();

        _reminderTimer.Tick += (s, e) =>
        {
            _ = ViewModel.CheckRemindersAsync();
            ViewModel.CheckScheduledRecordings();
        };
        _reminderTimer.Start();


        _settingsSaveDebounceTimer.Tick += (s, e) =>
        {
            _settingsSaveDebounceTimer.Stop();
            _ = ViewModel.SaveSettingsAsync();
        };

        _overlayHideTimer.Tick += (s, e) =>
        {
            _overlayHideTimer.Stop();
            HideFullScreenOverlay();
            HideWindowedVideoOverlay();
        };

        _volumeSaveDebounceTimer.Tick += (s, e) =>
        {
            _volumeSaveDebounceTimer.Stop();
            _ = SaveVolumeToSettingsAsync();
        };

        // Window-close save/exit logic lives in MainWindow (single per-window handler)
    }

        // Named handlers so Unloaded can unsubscribe from singleton VMs
        private void OnPlayerChangedApplyRenderer(object? s, EventArgs e)
        {
            // New channel/stream — drop pending archive seek from the previous one
            CancelPendingSeek();

            void ApplyPlayer()
            {
                var player = Player.Player;
                MediaPlayer.SetMediaPlayer(player);

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
        }

        private void OnPlayerArchiveStateChanged(object? s, EventArgs e) =>
            DispatcherQueue.TryEnqueue(UpdateArchiveBanner);

        private void OnPlayerVodStateChanged(object? s, EventArgs e) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateVodQualityButtons();
                UpdateArchivePauseButton();
            });

        private void OnRecordingChangedUpdateButtons(object? s, EventArgs e) =>
            DispatcherQueue.TryEnqueue(UpdateRecordButtons);

        private Task<int?> OnParentalUnlockRequested(ChannelViewModel channel) =>
            ShowParentalPinDialogAsync(channel);

        private void OnDailyLimitBlocked(object? s, EventArgs e) =>
            DispatcherQueue.TryEnqueue(async () => await ShowDailyLimitDialogAsync());

        private void OnDailyLimitReached(object? s, EventArgs e) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                StopPlayback();
                _ = ShowDailyLimitDialogAsync();
            });

        private void OnEpgVisibilityChanged(object? s, EventArgs e) =>
            DispatcherQueue.TryEnqueue(ApplyEpgVisibility);

        private void OnViewModelPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.SelectedChannel))
            {
                DispatcherQueue.TryEnqueue(UpdateEpgEmptyState);
            }
        }

        private void OnEpgReloaded(object? s, EventArgs e) =>
            DispatcherQueue.TryEnqueue(UpdateEpgEmptyState);

        private void OnEpgViewModelPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.EpgViewModel.IsLoading))
            {
                DispatcherQueue.TryEnqueue(UpdateEpgEmptyState);
            }
        }

        private void OnPlayerPropertyChangedBuffering(object? s, System.ComponentModel.PropertyChangedEventArgs e)
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

                if (!string.IsNullOrEmpty(Player.StreamError) &&
                    ViewModel.SelectedChannel != null &&
                    Player.CurrentPlayerChannelId == ViewModel.SelectedChannel.Id)
                {
                    ViewModel.SelectedChannel.IsPlaying = false;
                }
            });
        }

        private void OnPlayerPropertyChangedMuted(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlayerViewModel.IsMuted))
            {
                DispatcherQueue.TryEnqueue(UpdateMuteButtons);
            }
        }

        private void OnPlayerChangedAttachBuffering(object? s, EventArgs e)
        {
            _bufferingStallCount = 0;
            if (Player.Player != null)
            {
                _channelSessionStartUtc = DateTime.UtcNow;
                _bufferingStartedAtUtc = null;
                Player.Player.BufferingStarted += (_, _) =>
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _bufferingStallCount++;
                        _bufferingStartedAtUtc = DateTime.UtcNow;

                        Log.Information("Буферизация начата (простой #{Count}).", _bufferingStallCount);
                        UpdateStatsOverlay();
                    });
                Player.Player.BufferingEnded += (_, _) =>
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
        }

        private void OnRecordingChangedShowError(object? s, EventArgs e) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateRecordButtons();
                if (!string.IsNullOrEmpty(ViewModel.RecordError))
                {
                    ShowStreamError(ViewModel.RecordError);
                    ViewModel.RecordError = null;
                }
            });

        private void OnFilterChanged(object? s, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _syncingListSelection = true;
                try
                {
                    ChannelsListView.SelectedItem = ViewModel.SelectedChannel;
                    PosterGridView.SelectedItem = ViewModel.SelectedChannel;

                    OverlayChannelsListView.SelectedItem = ViewModel.SelectedChannel;
                }
                finally
                {
                    _syncingListSelection = false;
                }
            });

            DispatcherQueue.TryEnqueue(async () => await ScrollSelectedChannelIntoViewAsync());
        }

        private void OnReminderToastRequested(object? s, ProgramReminder e) => ShowReminderToast(e);

        private void OnSettingsSaveRequested(object? s, EventArgs e)
        {
            _settingsSaveDebounceTimer.Stop();
            _settingsSaveDebounceTimer.Start();
        }

        private void OnScrollToProgramRequested(object? s, EventArgs e) =>
            DispatcherQueue.TryEnqueue(async () => await ScrollToCurrentProgramAsync());

        private void OnArchivePlayErrorRequested(object? s, string e) =>
            DispatcherQueue.TryEnqueue(() => ShowStreamError(e));

        private void OnSleepTimerExpired(object? s, EventArgs e) =>
            DispatcherQueue.TryEnqueue(() =>
            {
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

        private void OnSleepTimerChanged(object? s, EventArgs e) =>
            DispatcherQueue.TryEnqueue(UpdateSleepTimerDisplays);

        private void UnsubscribeViewModelEvents()
        {
            Player.PlayerChanged -= OnPlayerChangedApplyRenderer;
            Player.ArchiveStateChanged -= OnPlayerArchiveStateChanged;
            Player.VodStateChanged -= OnPlayerVodStateChanged;
            Player.PropertyChanged -= OnPlayerPropertyChangedBuffering;
            Player.PropertyChanged -= OnPlayerPropertyChangedMuted;
            Player.PlayerChanged -= OnPlayerChangedAttachBuffering;

            ViewModel.PortalEpisodePickRequested -= OnPortalEpisodePickRequested;
            ViewModel.VodResumePromptRequested -= OnVodResumePromptRequested;
            ViewModel.RecordingChanged -= OnRecordingChangedUpdateButtons;
            ViewModel.RecordingChanged -= OnRecordingChangedShowError;
            ViewModel.ParentalUnlockRequested -= OnParentalUnlockRequested;
            ViewModel.DailyLimitBlocked -= OnDailyLimitBlocked;
            ViewModel.DailyLimitReached -= OnDailyLimitReached;
            ViewModel.EpgVisibilityChanged -= OnEpgVisibilityChanged;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.EpgViewModel.EpgReloaded -= OnEpgReloaded;
            ViewModel.EpgViewModel.PropertyChanged -= OnEpgViewModelPropertyChanged;
            ViewModel.FilterChanged -= OnFilterChanged;
            ViewModel.ReminderToastRequested -= OnReminderToastRequested;
            ViewModel.SettingsSaveRequested -= OnSettingsSaveRequested;
            ViewModel.ScrollToProgramRequested -= OnScrollToProgramRequested;
            ViewModel.ArchivePlayErrorRequested -= OnArchivePlayErrorRequested;
            ViewModel.SleepTimerExpired -= OnSleepTimerExpired;
            ViewModel.SleepTimerChanged -= OnSleepTimerChanged;

            UnsubscribeEpgEmptyStateChannel();

            if (_hotkeysAttached && XamlRoot?.Content is UIElement root)
            {
                root.PreviewKeyDown -= OnPagePreviewKeyDown;
                _hotkeysAttached = false;
            }
        }




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
        else if (e.Parameter is LocalVideoFile localVideo)
        {
            _localVideoFile = localVideo;
            _cameFromHub = true;
            Serilog.Log.Information("OnNavigatedTo: локальный видеофайл «{File}»", localVideo.Path);
        }
        else
        {
            Serilog.Log.Information("OnNavigatedTo: параметр = {Param}", e.Parameter?.ToString() ?? "NULL");
        }
    }


    private async Task OfferInterruptedRecordingsAsync()
    {
        try
        {
            var now = DateTime.Now;
            var resumable = ViewModel.AppSettings.InterruptedRecordings
                .Where(r => r.EndTime == null || r.EndTime > now)
                .ToList();

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
            var dialog = new ThemedContentDialog
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

    // Initialize page on load
    private async Task InitializeAsync()
    {

        var savedSettings = await _settingsService.LoadAsync();
        ViewModel.AppSettings = savedSettings;
        Serilog.Log.Information("InitializeAsync: ActivePlaylistId из настроек = {Id}, плейлистов = {Count}",
            savedSettings.ActivePlaylistId, savedSettings.Playlists.Count);


        await ViewModel.LoadVodResumePositionsAsync();

        var initialChannels = new List<ChannelViewModel>();


        ApplyTheme(savedSettings.Theme);
        ApplyInitialState();


        if (savedSettings.ChannelListWidth >= 240 && savedSettings.ChannelListWidth <= 640)
        {
            ChannelListColumn.Width = new GridLength(savedSettings.ChannelListWidth);
            _channelListExpandedWidth = savedSettings.ChannelListWidth;
        }

        {
            var saved = Math.Clamp(savedSettings.Volume, 0.0, 1.0);
            Player.LastUserVolume = saved;
            _isVolumeSliderSyncing = true;
            VideoOverlayVolumeSlider.Value = saved;
            OverlayVolumeSlider.Value = saved;
            _isVolumeSliderSyncing = false;
        }


        ApplyVideoStretch();

        Player.VideoUpscalerMode = VideoUpscaler.Normalize(savedSettings.VideoUpscaler);

        FrameServerPanel.Visibility = savedSettings.FrameServerRender
            ? Visibility.Visible
            : Visibility.Collapsed;
        VideoOverlayFrameServerItem.IsChecked = savedSettings.FrameServerRender;
        OverlayFrameServerItem.IsChecked = savedSettings.FrameServerRender;


        if (savedSettings.StatsOverlayVisible)
        {
            SetStatsOverlayVisible(show: true, persist: false);
        }

        if (ViewModel.AppSettings.Playlists.Count == 0 &&
            !string.IsNullOrWhiteSpace(savedSettings.PlaylistUrl))
        {
            ViewModel.AppSettings.Playlists.Add(new PlaylistSource
            {
                Id = 1,
                Name = DefaultPlaylistName(savedSettings.PlaylistUrl),
                Url = savedSettings.PlaylistUrl,
                LastWatchedChannel = savedSettings.LastWatchedChannel,


                EpgSources = savedSettings.EpgSources
                    .Select(s => new EPGSource { Url = s.Url, IsEnabled = s.IsEnabled })
                    .ToList()
            });
            ViewModel.AppSettings.ActivePlaylistId = 1;
            await _settingsService.SaveAsync(ViewModel.AppSettings);
        }

        if (_localVideoFile == null)
        {
            _activePlaylist = _navigatedPlaylist
                ?? ViewModel.AppSettings.Playlists
                    .FirstOrDefault(p => p.Id == ViewModel.AppSettings.ActivePlaylistId)
                ?? ViewModel.AppSettings.Playlists.FirstOrDefault();
            Serilog.Log.Information("InitializeAsync: _activePlaylist Id={Id} Name={Name} IsPortal={IsPortal}",
                _activePlaylist?.Id ?? -1, _activePlaylist?.Name ?? "NULL", _activePlaylist?.IsPortal ?? false);
            if (_activePlaylist != null)
            {
                ViewModel.AppSettings.ActivePlaylistId = _activePlaylist.Id;
                initialChannels.AddRange(await LoadPlaylistChannelsWithOverlayAsync(_activePlaylist));
                initialChannels = await ApplyChannelOverridesAsync(initialChannels);
            }

            await _channelRepository.Clear();
            var channelId = 1;
            foreach (var channel in initialChannels)
            {
                channel.Id = channelId++;
            }

            await _channelRepository.AddChannelsAsync(initialChannels);

            var channels = await _epgService.GetChannelsAsync();
            ViewModel.Channels = new ObservableCollection<ChannelViewModel>(channels);


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

            ViewModel.RefreshGroups();
            ViewModel.FilterChannels();
        }
        else
        {
            Serilog.Log.Information("InitializeAsync: локальный видеофайл — плейлист и EPG не загружаются");
            ViewModel.Channels = new ObservableCollection<ChannelViewModel>();
            ViewModel.UpdateChannelCountText();

            ChannelListPanel.Visibility = Visibility.Collapsed;
            ChannelListSplitter.Visibility = Visibility.Collapsed;
            ChannelListSplitterGrip.Visibility = Visibility.Collapsed;
            ChannelListColumn.MinWidth = 0;
            ChannelListColumn.Width = new GridLength(0);
        }


        if (_cameFromHub)
        {
            BackToHubButton.Visibility = Visibility.Visible;
            ChannelsHeaderText.Visibility = Visibility.Collapsed;
        }

        UpdatePlaylistMenu();

        ScheduleAutoUpdateCheck();
        ApplyChannelViewMode();

        _ = OfferInterruptedRecordingsAsync();

        var autoResume = !_skipResume && _vodResumeChannelTitle == null && _localVideoFile == null;
        if (ViewModel.Channels.Count > 0 && autoResume)
        {

            var lastWatchedName = _activePlaylist?.LastWatchedChannel
                                  ?? ViewModel.AppSettings.LastWatchedChannel;
            var lastWatched = string.IsNullOrWhiteSpace(lastWatchedName)
                ? null
                : ViewModel.Channels.FirstOrDefault(c =>
                    string.Equals(c.Name, lastWatchedName, StringComparison.OrdinalIgnoreCase));

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


        if (_vodResumeChannelTitle != null)
        {
            _ = ResumeVodFromHubAsync(_vodResumeChannelTitle, _vodResumeEpisodeIndex);
        }


        if (_localVideoFile != null)
        {
            _ = PlayLocalVideoFileAsync(_localVideoFile);
        }

        await Task.Yield();

        if (_localVideoFile == null)
        {

            await ViewModel.EpgViewModel.LoadEPGAsync();

            if (ViewModel.SelectedChannel is { } selected)
            {
                await ViewModel.EpgViewModel.LoadEPGForChannelAsync(selected.Id);
            }
        }

        ViewModel.ApplyReminderFlags();
    }


    private string? _pendingUpdateSetupPath;


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

            _logger.LogWarning(ex, "Автообновление: шаг не удался, текущая версия продолжает работать.");
        }
    }


    private async Task OfferUpdateInstallAsync(Version version, string setupPath)
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

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {


            App.PendingUpdateSetupPath = setupPath;
            _logger.LogInformation("Обновление {Version}: пользователь отложил установку до закрытия приложения.", version);
            return;
        }

        if (ViewModel.Recording.Active.Count > 0)
        {


            _pendingUpdateSetupPath = setupPath;
            ViewModel.Recording.RecordingsChanged += OnRecordingsChanged_InstallUpdate;
            _logger.LogInformation(
                "Обновление {Version} отложено: идут записи ({Count}), установится после их окончания.",
                version, ViewModel.Recording.Active.Count);

            var info = new ThemedContentDialog
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


        ViewModel.Recording.RecordingsChanged -= OnRecordingsChanged_InstallUpdate;
        var setupPath = _pendingUpdateSetupPath;
        _pendingUpdateSetupPath = null;
        DispatcherQueue.TryEnqueue(() => _updateService.RunInstallerAndExit(setupPath));
    }


    private void ApplyChannelViewMode()
    {

        var posters = ViewModel.IsContentTypeFilterVisible == Visibility.Visible && ViewModel.AppSettings.ChannelListPosterView;
        PosterGridView.Visibility = posters ? Visibility.Visible : Visibility.Collapsed;
        ChannelsListView.Visibility = posters ? Visibility.Collapsed : Visibility.Visible;
        PosterViewIconList.Visibility = posters ? Visibility.Collapsed : Visibility.Visible;
        PosterViewIconList2.Visibility = posters ? Visibility.Collapsed : Visibility.Visible;
        PosterViewIconList3.Visibility = posters ? Visibility.Collapsed : Visibility.Visible;
        PosterViewIconGrid.Visibility = posters ? Visibility.Visible : Visibility.Collapsed;

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


    private void ApplyTheme(string theme)
    {
        App.ApplyAppTheme(theme);

        UpdateMuteButtons();
        UpdateRecordButtons();
        UpdateArchivePauseButton();
    }


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

    private void ChannelsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ChannelViewModel channel)
        {
            ViewModel.SelectAndPlayChannelCommand.Execute(channel);
        }
    }

    private async void AddChannelButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ThemedContentDialog
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


    private Task PlayLiveAsync(ChannelViewModel channel) => ViewModel.PlayChannelAsync(channel, interactive: false);

    private void StopPlayback() => Player.Stop();

    private void EPGProgramsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EPGEntry entry)
        {
            ViewModel.PlayArchiveEntryCommand.Execute(entry);
        }
    }


    private bool _syncingListSelection;


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
