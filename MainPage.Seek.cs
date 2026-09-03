using System;
using System.Linq;
using System.Threading.Tasks;
using IptvPlayer.Controls;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace IptvPlayer;

/// <summary>
/// VOD seek, archive seek, archive banner, EPG visibility, fullscreen buttons,
/// parental PIN dialog, and EPG scroll.
/// </summary>
public sealed partial class MainPage : Page
{
    private void UpdateArchivePauseButton()
    {
        // Пауза доступна и в архиве, и в VOD (ToggleArchivePause работает
        // в обоих режимах), а «В эфир» и архивный seek-таймлайн — только
        // в архиве: VOD перематывается своей VodSeekPanel.
        var isPauseAvailable = (Player.IsArchivePlaying || Player.IsVodPlaying) && Player.Player != null;
        var isArchiveActive = Player.IsArchivePlaying && Player.Player != null;
        OverlayPauseButton.Visibility = isPauseAvailable ? Visibility.Visible : Visibility.Collapsed;
        VideoOverlayPauseButton.Visibility = isPauseAvailable ? Visibility.Visible : Visibility.Collapsed;
        OverlayBackToLiveButton.Visibility = isArchiveActive ? Visibility.Visible : Visibility.Collapsed;
        VideoOverlayBackToLiveButton.Visibility = isArchiveActive ? Visibility.Visible : Visibility.Collapsed;

        // Канал паузы: у SelectedChannel приоритет — у каналов портала и
        // локальных файлов Id не уникален (все нули), поиск по Id в списке
        // вернул бы первый попавшийся канал с чужим IsPlaying. Локальный файл
        // (карточка «Видео») в списке вообще не состоит.
        var channel = ViewModel.SelectedChannel is { } selected &&
                      Player.CurrentPlayerChannelId == selected.Id
            ? selected
            : Player.CurrentPlayerChannelId.HasValue &&
              ViewModel.Channels.FirstOrDefault(c => c.Id == Player.CurrentPlayerChannelId.Value)
                  is { } listed
                ? listed
                : ViewModel.SelectedChannel;
        var isPaused = isPauseAvailable && channel is { IsPlaying: false };

        WindowedArchiveSeekPanel.Visibility = isArchiveActive ? Visibility.Visible : Visibility.Collapsed;
        OverlayArchiveSeekPanel.Visibility = isArchiveActive ? Visibility.Visible : Visibility.Collapsed;
        if (isArchiveActive)
        {
            Player.RefreshArchivePosition();
            UpdateArchiveSeekBar();
        }

        OverlayPauseButton.Content = isPaused ? AppIcons.Play(20) : AppIcons.Pause(20);
        ToolTipService.SetToolTip(OverlayPauseButton, isPaused ? L.T("Prodolzhit_Probel") : L.T("Pauza_Probel"));
        VideoOverlayPauseButton.Content = isPaused ? AppIcons.Play(16) : AppIcons.Pause(16);
        ToolTipService.SetToolTip(VideoOverlayPauseButton, isPaused ? L.T("Prodolzhit_Probel") : L.T("Pauza_Probel"));

        ShowPlaybackStateBadge(isPauseAvailable, isPaused);
    }

    // Состояние паузы на предыдущем вызове UpdateArchivePauseButton:
    // null — воспроизведения нет (старт/остановка), индикатор не показываем.
    private bool? _lastBadgeState;
    private DispatcherQueueTimer? _badgeHideTimer;

    private void ShowPlaybackStateBadge(bool isPauseAvailable, bool isPaused)
    {
        if (!isPauseAvailable)
        {
            // Плеер остановился — следующая пауза снова получает индикатор.
            _lastBadgeState = null;
            return;
        }

        if (_lastBadgeState == isPaused)
        {
            return;
        }

        var isFirstState = _lastBadgeState == null;
        _lastBadgeState = isPaused;
        if (isFirstState)
        {
            // Первый расчёт после старта воспроизведения — не событие паузы.
            return;
        }

        PlaybackStateBadgeIcon.Content = isPaused ? AppIcons.Play(22) : AppIcons.Pause(22);
        PlaybackStateBadgeText.Text = L.T(isPaused ? "Badge_Pauza" : "Badge_Vosproizvedenie");
        AnimateBadgeOpacity(1);

        _badgeHideTimer ??= DispatcherQueue.CreateTimer();
        _badgeHideTimer.Stop();
        _badgeHideTimer.Interval = TimeSpan.FromMilliseconds(900);
        _badgeHideTimer.Tick += BadgeHideTimer_Tick;
        _badgeHideTimer.Start();
    }

    private void BadgeHideTimer_Tick(object? sender, object e)
    {
        _badgeHideTimer?.Stop();
        AnimateBadgeOpacity(0);
    }

    private void AnimateBadgeOpacity(double to)
    {
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new QuadraticEase()
        };
        Storyboard.SetTarget(animation, PlaybackStateBadge);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void UpdateArchiveBanner()
    {
        if (Player.IsArchivePlaying && Player.ArchiveEntry != null)
        {
            var title = string.Format(L.T("Arkhiv_0_1"), Player.ArchiveEntry.ProgramName, $"{Player.ArchiveEntry.StartTime:dd.MM HH:mm}");

            OverlayArchiveText.Text = string.Format(L.T("Arkhiv_0"), Player.ArchiveEntry.ProgramName);
            OverlayArchiveIndicator.Visibility = Visibility.Visible;

            WindowedArchiveText.Text = string.Format(L.T("Arkhiv_0"), Player.ArchiveEntry.ProgramName);
            WindowedArchiveIndicator.Visibility = Visibility.Visible;

            ToolTipService.SetToolTip(VideoOverlayBackToLiveButton, title);
            ToolTipService.SetToolTip(OverlayBackToLiveButton, title);
        }
        else
        {
            OverlayArchiveIndicator.Visibility = Visibility.Collapsed;
            WindowedArchiveIndicator.Visibility = Visibility.Collapsed;
        }

        UpdateArchivePauseButton();
    }

    // ===================== VOD Quality =====================

    private void UpdateVodQualityButtons()
    {
        var visible = Player.IsVodPlaying && Player.VodQualities.Count > 1;
        OverlayVodQualityButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        WindowedVodQualityButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        OverlayVodSeekPanel.Visibility = Player.IsVodPlaying ? Visibility.Visible : Visibility.Collapsed;
        WindowedVodSeekPanel.Visibility = Player.IsVodPlaying ? Visibility.Visible : Visibility.Collapsed;

        UpdateVodSeasonEpisodeCombos();

        var epgVisible = Player.IsVodPlaying ? Visibility.Collapsed : Visibility.Visible;
        VideoOverlayEpgButton.Visibility = epgVisible;
        OverlayEpgButton.Visibility = epgVisible;

        if (Player.CurrentVodQuality is { } quality)
        {
            OverlayVodQualityButton.Content = quality;
            WindowedVodQualityButton.Content = quality;
        }

        if (!visible)
        {
            return;
        }

        var menu = new MenuFlyout();
        foreach (var option in Player.VodQualities)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = option,
                IsChecked = option == Player.CurrentVodQuality,
                Tag = option
            };
            item.Click += VodQualityMenuItem_Click;
            menu.Items.Add(item);
        }

        var menuCopy = new MenuFlyout();
        foreach (var option in Player.VodQualities)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = option,
                IsChecked = option == Player.CurrentVodQuality,
                Tag = option
            };
            item.Click += VodQualityMenuItem_Click;
            menuCopy.Items.Add(item);
        }

        OverlayVodQualityButton.Flyout = menu;
        WindowedVodQualityButton.Flyout = menuCopy;
    }

    private async void VodQualityMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string quality })
        {
            await Player.SwitchVodQualityAsync(quality);
        }
    }

    // ===================== VOD Season/Episode =====================

    private bool _updatingVodCombos;

    private void UpdateVodSeasonEpisodeCombos()
    {
        _updatingVodCombos = true;
        try
        {
            var seasonsVisible = false;
            var episodesVisible = false;

            if (Player.IsVodPlaying && Player.VodChannel is { } vodChannel)
            {
                var siblings = ViewModel.GetPortalSeasonSiblings(vodChannel);
                seasonsVisible = siblings.Count > 1;
                if (seasonsVisible)
                {
                    foreach (var combo in new[] { WindowedVodSeasonCombo, OverlayVodSeasonCombo })
                    {
                        combo.Items.Clear();
                        foreach (var sibling in siblings)
                        {
                            combo.Items.Add(new ComboBoxItem
                            {
                                Content = SeasonLabel(sibling.Name),
                                Tag = sibling,
                                IsSelected = ReferenceEquals(sibling, vodChannel)
                            });
                        }
                    }
                }

                episodesVisible = Player.VodEpisodes.Count > 1;
                if (episodesVisible)
                {
                    foreach (var combo in new[] { WindowedVodEpisodeCombo, OverlayVodEpisodeCombo })
                    {
                        combo.Items.Clear();
                        for (var i = 0; i < Player.VodEpisodes.Count; i++)
                        {
                            combo.Items.Add(new ComboBoxItem
                            {
                                Content = $"{i + 1}. {Player.VodEpisodes[i].Title}",
                                Tag = i,
                                IsSelected = i == Player.CurrentVodEpisodeIndex
                            });
                        }
                    }
                }
            }

            WindowedVodSeasonCombo.Visibility = OverlayVodSeasonCombo.Visibility =
                seasonsVisible ? Visibility.Visible : Visibility.Collapsed;
            WindowedVodEpisodeCombo.Visibility = OverlayVodEpisodeCombo.Visibility =
                episodesVisible ? Visibility.Visible : Visibility.Collapsed;

            if (!seasonsVisible)
            {
                WindowedVodSeasonCombo.Items.Clear();
                OverlayVodSeasonCombo.Items.Clear();
            }

            if (!episodesVisible)
            {
                WindowedVodEpisodeCombo.Items.Clear();
                OverlayVodEpisodeCombo.Items.Clear();
            }
        }
        finally
        {
            _updatingVodCombos = false;
        }
    }

    private static string SeasonLabel(string name) =>
        MainPageViewModel.ParsePortalSeasonName(name).Season is { } season
            ? (season.From == season.To
                ? string.Format(L.T("Sezon_0"), season.From)
                : string.Format(L.T("Sezon_0_1"), season.From, season.To))
            : name;

    private async void VodSeasonCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingVodCombos ||
            sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: ChannelViewModel sibling } })
        {
            return;
        }

        if (!ReferenceEquals(sibling, Player.VodChannel))
        {
            await ViewModel.PlayChannelAsync(sibling, interactive: false);
        }
    }

    private async void VodEpisodeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingVodCombos ||
            sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: int index } })
        {
            return;
        }

        if (index != Player.CurrentVodEpisodeIndex)
        {
            await Player.PlayVodEpisodeAsync(index);
        }
    }

    // ===================== VOD Seek =====================

    private bool _updatingVodSeekBarValue;
    private Slider? _activeVodSlider;

    private void UpdateVodSeekBar()
    {
        if (!Player.IsVodPlaying)
        {
            return;
        }

        WindowedVodPositionText.Text = Player.VodPositionText;
        OverlayVodPositionText.Text = Player.VodPositionText;
        WindowedVodDurationText.Text = Player.VodDurationText;
        OverlayVodDurationText.Text = Player.VodDurationText;

        if (Player.IsVodSeeking)
        {
            return;
        }

        var duration = Math.Max(1.0, Player.VodDurationSeconds);
        var position = Math.Clamp(Player.VodPositionSeconds, 0.0, duration);

        _updatingVodSeekBarValue = true;
        try
        {
            WindowedVodSeekBar.Maximum = duration;
            WindowedVodSeekBar.Value = position;
            OverlayVodSeekBar.Maximum = duration;
            OverlayVodSeekBar.Value = position;
        }
        finally
        {
            _updatingVodSeekBarValue = false;
        }
    }

    private void VodSeekBar_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (sender is not Slider slider || _updatingVodSeekBarValue)
        {
            return;
        }

        _activeVodSlider = slider;
        Player.IsVodSeeking = true;

        var text = PlayerViewModel.FormatArchiveTime(slider.Value);
        WindowedVodPositionText.Text = text;
        OverlayVodPositionText.Text = text;
    }

    private void VodSeekBar_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            _activeVodSlider = slider;
        }
        Player.IsVodSeeking = true;
    }

    private void VodSeekBar_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => CommitVodSeek();

    private void VodSeekBar_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => CommitVodSeek();

    private void CommitVodSeek()
    {
        Player.IsVodSeeking = false;
        if (_activeVodSlider == null || !Player.IsVodPlaying)
        {
            return;
        }

        var target = _activeVodSlider.Value;
        _activeVodSlider = null;
        Player.SeekVod(target);
    }

    // ===================== Archive Seek =====================

    private void UpdateArchiveSeekBar()
    {
        if (!Player.IsArchivePlaying)
        {
            return;
        }

        WindowedArchivePositionText.Text = Player.ArchivePositionText;
        OverlayArchivePositionText.Text = Player.ArchivePositionText;
        WindowedArchiveDurationText.Text = Player.ArchiveDurationText;
        OverlayArchiveDurationText.Text = Player.ArchiveDurationText;

        if (Player.IsArchiveSeeking)
        {
            return;
        }

        var duration = Math.Max(1.0, Player.ArchiveDurationSeconds);
        var position = Math.Clamp(Player.ArchivePositionSeconds, 0.0, duration);

        _updatingSeekBarValue = true;
        try
        {
            WindowedArchiveSeekBar.Maximum = duration;
            WindowedArchiveSeekBar.Value = position;
            OverlayArchiveSeekBar.Maximum = duration;
            OverlayArchiveSeekBar.Value = position;
        }
        finally
        {
            _updatingSeekBarValue = false;
        }
    }

    private void ArchiveSeekBar_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (sender is not Slider slider || _updatingSeekBarValue)
        {
            return;
        }

        _activeSeekSlider = slider;
        Player.IsArchiveSeeking = true;

        var text = PlayerViewModel.FormatArchiveTime(slider.Value);
        WindowedArchivePositionText.Text = text;
        OverlayArchivePositionText.Text = text;

        _archiveSeekDebounceTimer.Stop();
        _archiveSeekDebounceTimer.Start();
    }

    private void ArchiveSeekBar_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            _activeSeekSlider = slider;
        }
        Player.IsArchiveSeeking = true;
    }

    private void ArchiveSeekBar_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => CommitArchiveSeek();

    private void ArchiveSeekBar_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => CommitArchiveSeek();

    private void CommitArchiveSeek()
    {
        _archiveSeekDebounceTimer.Stop();
        Player.IsArchiveSeeking = false;

        if (_activeSeekSlider == null || !Player.IsArchivePlaying)
        {
            return;
        }

        var target = _activeSeekSlider.Value;
        _activeSeekSlider = null;

        _ = Player.SeekArchiveAsync(target);
    }

    // ===================== EPG visibility =====================

    private void ApplyEpgVisibility()
    {
        var visible = ViewModel.IsEpgVisible;
        EpgPanelBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        EpgScrimBorder.Visibility = EpgPanelBorder.Visibility;
        Serilog.Log.Debug("EPG: {Vis} (fullScreen={FS})", visible ? "open" : "close", _isFullScreen);

        if (_isFullScreen && visible)
        {
            Serilog.Log.Debug("EPG: fullscreen — скрыть оверлей, показать курсор для EPG-панели");
            HideFullScreenOverlay(immediate: true);
            _overlayHideTimer.Stop();
            ShowCursorOverVideo();
        }

        ToolTipService.SetToolTip(VideoOverlayEpgButton, visible ? L.T("Skryt_EPG") : L.T("Pokazat_EPG_Lbl"));

        UpdateEpgEmptyState();

        if (visible)
        {
            _ = ScrollToCurrentProgramAsync();
        }
    }

    // Канал, у которого сейчас подписаны EPGEntries.CollectionChanged:
    // пересоздаётся при каждом выборе канала.
    private ChannelViewModel? _epgEmptyStateChannel;

    /// <summary>
    /// Показывает «Программа недоступна» вместо пустой сетки, когда EPG-панель
    /// открыта, а у выбранного канала нет ни одной программы (и загрузка EPG
    /// уже завершилась — во время загрузки пустой список ещё не приговор).
    /// </summary>
    private void UpdateEpgEmptyState()
    {
        // Подписка на заполнение EPGEntries выбранного канала: коллекция
        // наполняется фоном после выбора канала, поэтому одного вызова при
        // смене канала недостаточно.
        var channel = ViewModel.SelectedChannel;
        if (!ReferenceEquals(channel, _epgEmptyStateChannel))
        {
            if (_epgEmptyStateChannel != null)
            {
                _epgEmptyStateChannel.EPGEntries.CollectionChanged -= OnEpgEntriesChanged;
            }
            _epgEmptyStateChannel = channel;
            if (channel != null)
            {
                channel.EPGEntries.CollectionChanged += OnEpgEntriesChanged;
            }
        }

        var showEmpty = ViewModel.IsEpgVisible &&
                        channel != null &&
                        channel.EPGEntries.Count == 0 &&
                        !ViewModel.EpgViewModel.IsLoading;

        EmptyChannelEPGState.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEpgEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdateEpgEmptyState);
    }

    private void EpgScrimBorder_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        ViewModel.IsEpgVisible = false;
        ApplyEpgVisibility();
    }

    // ===================== Fullscreen buttons =====================

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {        SetFullScreenMode(!_isFullScreen);
    }

    private void ExitFullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        SetFullScreenMode(false);
    }

    // ===================== Parental PIN dialog =====================

    private Task<int?>? _pinDialogInProgress;

    private async Task<int?> ShowParentalPinDialogAsync(ChannelViewModel channel)
    {
        if (_pinDialogInProgress != null)
        {
            Serilog.Log.Warning("Повторный запрос PIN при открытом диалоге — присоединяемся к нему.");
            return await _pinDialogInProgress;
        }

        var task = ShowParentalPinDialogCoreAsync(channel);
        _pinDialogInProgress = task;
        try
        {
            return await task;
        }
        finally
        {
            _pinDialogInProgress = null;
        }
    }

    private async Task<int?> ShowParentalPinDialogCoreAsync(ChannelViewModel channel)
    {
        Serilog.Log.Information("PIN-диалог открыт для канала {Channel}.", channel.Name);
        var tcs = new TaskCompletionSource<int?>();

        var pinBox = new PasswordBox { PlaceholderText = "PIN", Width = 200, HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left };
        var errorText = new TextBlock
        {
            FontSize = 12,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            TextWrapping = TextWrapping.Wrap
        };

        void TryUnlock(int minutes)
        {
            if (ParentalControlService.VerifyPin(ViewModel.AppSettings, pinBox.Password))
            {
                Serilog.Log.Information("PIN верен — отключение запроса на {Minutes} мин.", minutes);
                tcs.TrySetResult(minutes);
                _pinDialog?.Hide();
            }
            else
            {
                Serilog.Log.Warning("Введен неверный PIN (канал {Channel}).", channel.Name);
                errorText.Text = L.T("Nevernyy_PIN");
            }
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing =8 };
        foreach (var (label, minutes) in new (string, int)[]
                 {
                     (L.T("15_Min_Lbl"), 15),
                     (L.T("30_Min_Lbl"), 30),
                     (L.T("45_Min_Lbl"), 45),
                     (L.T("1_Chas_Lbl"), 60),
                     (L.T("Do_Vyklyucheniya"), 0),
                 })
        {
            var captured = minutes;
            var b = new Button { Content = label };
            b.Click += (s, e) => TryUnlock(captured);
            buttons.Children.Add(b);
        }

        var panel = new StackPanel { Spacing = 12, Width = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = string.Format(L.T("Kanal_0_Zakryt_Roditelskim_Kontrolem_Vvedite"), channel.Name, channel.Name),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(pinBox);
        panel.Children.Add(errorText);
        panel.Children.Add(buttons);

        var dialog = new ThemedContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = L.T("Roditelskiy_Kontrol_Lbl"),
            Content = panel,
            CloseButtonText = L.T("Otmena_Lbl")
        };
        dialog.Closed += (s, e) => tcs.TrySetResult(null);
        _pinDialog = dialog;
        await dialog.ShowAsync();
        _pinDialog = null;
        Serilog.Log.Information("PIN-диалог закрыт, результат: {Result}.", await tcs.Task);

        return await tcs.Task;
    }

    private ContentDialog? _pinDialog;

    // ===================== EPG scroll =====================

    private async Task ScrollToCurrentProgramAsync()
    {
        if (ViewModel.SelectedChannel == null) return;

        var entries = ViewModel.SelectedChannel.EPGEntries;
        if (entries.Count == 0) return;

        await Task.Yield();
        await Task.Delay(50);

        var currentEntry = entries.FirstOrDefault(e => e.IsCurrent);
        if (currentEntry != null)
        {
            EPGProgramsListView.ScrollIntoView(currentEntry);
        }
        else
        {
            var now = DateTime.Now;
            var nextEntry = entries.FirstOrDefault(e => e.StartTime > now);
            if (nextEntry != null)
            {
                EPGProgramsListView.ScrollIntoView(nextEntry);
            }
            else if (entries.Count > 0)
            {
                EPGProgramsListView.ScrollIntoView(entries[0]);
            }
        }
    }

    private void EPGProgramsListView_Loaded(object sender, RoutedEventArgs e)
    {
        _ = ScrollToCurrentProgramAsync();
    }
}
