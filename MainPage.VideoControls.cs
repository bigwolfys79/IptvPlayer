using System;
using System.Diagnostics;
using System.Threading.Tasks;
using IptvPlayer.Controls;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace IptvPlayer;

/// <summary>
/// Volume, mute, video stretch, sleep timer, mini player, record buttons.
/// </summary>
public sealed partial class MainPage : Page
{
    private void OverlayVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => OnVolumeSliderChanged(e.NewValue);

    private void VideoOverlayVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => OnVolumeSliderChanged(e.NewValue);

    private void OnVolumeSliderChanged(double value)
    {
        if (_isVolumeSliderSyncing)
        {
            return;
        }

        Player.LastUserVolume = value;

        Player.ClearMute();

        if (Player.Player != null)
        {
            Player.Player.Volume = value;
        }

        SyncVolumeSliders(value);

        _volumeSaveDebounceTimer.Stop();
        _volumeSaveDebounceTimer.Start();
    }

    /// <summary>
    /// Программно выставляет оба слайдера громкости в одно значение,
    /// не провоцируя обратные события ValueChanged.
    /// </summary>
    private void SyncVolumeSliders(double value)
    {
        _isVolumeSliderSyncing = true;
        if (Math.Abs(OverlayVolumeSlider.Value - value) > 0.001)
        {
            OverlayVolumeSlider.Value = value;
        }
        if (Math.Abs(VideoOverlayVolumeSlider.Value - value) > 0.001)
        {
            VideoOverlayVolumeSlider.Value = value;
        }
        _isVolumeSliderSyncing = false;
    }

    private async Task SaveVolumeToSettingsAsync()
    {
        try
        {
            ViewModel.AppSettings.Volume = Player.LastUserVolume ?? 1.0;
            await _settingsService.SaveAsync(ViewModel.AppSettings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить громкость.");
        }
    }

    // ===================== Мьют =====================

    private void MuteButton_Click(object sender, RoutedEventArgs e) => Player.ToggleMute();

    /// <summary>
    /// Кнопки M в обеих панелях: иконка (динамик/динамик с крестом), подсказка
    /// и слайдеры (в mute показывают ноль — синхронизация программная и
    /// LastUserVolume не затирает).
    /// </summary>
    private void UpdateMuteButtons()
    {
        VideoOverlayMuteButton.Content = Player.IsMuted ? AppIcons.SpeakerMuted(16) : AppIcons.SpeakerOn(16);
        OverlayMuteButton.Content = Player.IsMuted ? AppIcons.SpeakerMuted(18) : AppIcons.SpeakerOn(18);

        var tooltip = Player.IsMuted
            ? L.T("Vklyuchit_Zvuk_M")
            : L.T("Bez_Zvuka_M_Lbl");
        ToolTipService.SetToolTip(VideoOverlayMuteButton, tooltip);
        ToolTipService.SetToolTip(OverlayMuteButton, tooltip);

        SyncVolumeSliders(Player.IsMuted ? 0.0 : Player.LastUserVolume ?? Player.Player?.Volume ?? 1.0);
    }

    // ===================== Двойной клик / полноэкранный режим =====================

    /// <summary>
    /// Двойной клик по видео — переключение полноэкранного режима.
    /// </summary>
    private void VideoArea_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        SetFullScreenMode(!_isFullScreen);
        e.Handled = true;
    }

    /// <summary>
    /// Двойной клик по видимому полноэкранному оверлею — только по фону/шапке.
    /// </summary>
    private void FullScreenOverlay_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            AnyAncestorOrSelf(source, element => IsInteractiveControl(element)))
        {
            return;
        }

        SetFullScreenMode(!_isFullScreen);
        e.Handled = true;
    }

    /// <summary>
    /// Элементы, чьи двойные клики принадлежат им самим — полноэкранный режим
    /// они переключать не должны.
    /// </summary>
    private static bool IsInteractiveControl(DependencyObject element) =>
        element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
            or Slider or ListView or ComboBox or TextBox or AutoSuggestBox;

    // ===================== Режимы отображения видео =====================

    private void StretchButton_Click(object sender, RoutedEventArgs e) => CycleVideoStretch();

    /// <summary>Строковый режим настроек → Stretch медиаэлемента.</summary>
    private static Stretch ParseStretch(string? value) => value switch
    {
        "Fill" => Stretch.Fill,
        "UniformToFill" => Stretch.UniformToFill,
        _ => Stretch.Uniform
    };

    /// <summary>Применяет сохранённый режим отображения (старт приложения).</summary>
    private void ApplyVideoStretch()
    {
        var stretch = ParseStretch(ViewModel.AppSettings.VideoStretch);
        MediaPlayer.Stretch = stretch;
        // При frame server-рендере кадр рисует FrameServerRenderer, режим
        // применяем и там (иначе кнопка работает только на обычном пути).
        _frameServerRenderer.VideoStretchMode = stretch;
        UpdateStretchButtons();
    }

    private void CycleVideoStretch()
    {
        var next = MediaPlayer.Stretch switch
        {
            Stretch.Uniform => Stretch.Fill,
            Stretch.Fill => Stretch.UniformToFill,
            _ => Stretch.Uniform
        };

        MediaPlayer.Stretch = next;
        _frameServerRenderer.VideoStretchMode = next;
        ViewModel.AppSettings.VideoStretch = next.ToString();
        UpdateStretchButtons();

        _settingsSaveDebounceTimer.Stop();
        _settingsSaveDebounceTimer.Start();
    }

    private void UpdateStretchButtons()
    {
        var mode = MediaPlayer.Stretch switch
        {
            Stretch.Fill => L.T("Stretch_Rastyanut"),
            Stretch.UniformToFill => L.T("Stretch_Oberezat"),
            _ => L.T("Stretch_Vpisat")
        };
        var tooltip = string.Format(L.T("Rezhim_Otobrazheniya_0_V"), mode);
        ToolTipService.SetToolTip(VideoOverlayStretchButton, tooltip);
        ToolTipService.SetToolTip(OverlayStretchButton, tooltip);
    }

    // ===================== Улучшение картинки (апскейлер) =====================

    /// <summary>
    /// Перед открытием меню кнопки отмечаем текущий пресет: RadioMenuFlyoutItem
    /// не синхронизируется сам — группировка даёт только взаимоисключающий выбор.
    /// </summary>
    private void UpscalerMenu_Opening(object? sender, object e)
    {
        var mode = Player.VideoUpscalerMode;
        UpscalerOffItem.IsChecked = mode == VideoUpscaler.Off;
        UpscalerSharpItem.IsChecked = mode == VideoUpscaler.Sharp;
        UpscalerDenoiseItem.IsChecked = mode == VideoUpscaler.Denoise;
        UpscalerSdItem.IsChecked = mode == VideoUpscaler.SdUpscale;

        OverlayUpscalerOffItem.IsChecked = UpscalerOffItem.IsChecked;
        OverlayUpscalerSharpItem.IsChecked = UpscalerSharpItem.IsChecked;
        OverlayUpscalerDenoiseItem.IsChecked = UpscalerDenoiseItem.IsChecked;
        OverlayUpscalerSdItem.IsChecked = UpscalerSdItem.IsChecked;
    }

    private async void UpscalerItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.MenuFlyoutItem item &&
            item.Tag is string mode)
        {
            await Player.SetVideoUpscalerAsync(mode);
        }
    }

    // ===================== Рендер-апскейл (frame server, эксперимент) =====================

    /// <summary>
    /// Переключение рендер-пути frame server: плеер создаётся с флагом
    /// IsVideoFrameServerEnabled при открытии потока, поэтому смена режима
    /// требует перезапуска текущего канала. Отрисовка — FrameServerRenderer
    /// в CanvasSwapChainPanel поверх MediaPlayerElement.
    /// </summary>
    private async void FrameServerItem_Click(object sender, RoutedEventArgs e)
    {
        var enable = !ViewModel.AppSettings.FrameServerRender;
        ViewModel.AppSettings.FrameServerRender = enable;
        VideoOverlayFrameServerItem.IsChecked = enable;
        OverlayFrameServerItem.IsChecked = enable;
        FrameServerPanel.Visibility = enable ? Visibility.Visible : Visibility.Collapsed;

        _settingsSaveDebounceTimer.Stop();
        _settingsSaveDebounceTimer.Start();

        _logger.LogInformation("Рендер-апскейл (frame server): {State}.", enable ? "вкл" : "выкл");

        // Перезапускаем текущий канал: флаг IsVideoFrameServerEnabled
        // задаётся при создании плеера и в обе стороны требует
        // пересоздания (PlayerChanged привяжет/отвяжет рендер сам).
        if (ViewModel.SelectedChannel is { } channel)
        {
            await ViewModel.PlayChannelAsync(channel);
        }
    }

    // ===================== Таймер сна =====================

    private async void SleepTimerButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ThemedContentDialog
        {
            Title = L.T("Taymer_Sna_Lbl"),
            PrimaryButtonText = L.T("Ustanovit"),
            CloseButtonText = L.T("Otmena_Lbl"),
            XamlRoot = ((Button)sender).XamlRoot
        };

        var panel = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Vertical, Spacing = 12 };
        var timeOptions = new[] { 15, 30, 45, 60, 90, 120 };
        var radioPanel = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Vertical, Spacing = 4 };

        if (ViewModel.IsSleepTimerActive)
        {
            radioPanel.Children.Add(new RadioButton
            {
                Content = L.T("Otklyuchit_Taymer"),
                Tag = 0,
                GroupName = "SleepTimer"
            });
        }

        foreach (var minutes in timeOptions)
        {
            radioPanel.Children.Add(new RadioButton
            {
                Content = string.Format(L.T("0_Min"), minutes, minutes),
                Tag = minutes,
                GroupName = "SleepTimer"
            });
        }

        var customBox = new TextBox
        {
            Header = L.T("Svoe_Znachenie_Minuty"),
            PlaceholderText = "60",
            Width = 200
        };

        var action = ViewModel.AppSettings.SleepTimerAction switch
        {
            "Exit" => L.T("SleepTimer_Exit"),
            "Shutdown" => L.T("SleepTimer_Shutdown"),
            _ => L.T("SleepTimer_Stop")
        };
        var actionHint = new TextBlock
        {
            Text = string.Format(L.T("Po_Istechenii_Taymera_0"), action),
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        };

        panel.Children.Add(radioPanel);
        panel.Children.Add(customBox);
        panel.Children.Add(actionHint);
        dialog.Content = panel;

        dialog.PrimaryButtonClick += (s, args) =>
        {
            var selectedMinutes = 0;

            foreach (var child in radioPanel.Children)
            {
                if (child is RadioButton { IsChecked: true, Tag: int tag })
                {
                    selectedMinutes = tag;
                    break;
                }
            }

            if (selectedMinutes == 0 && int.TryParse(customBox.Text.Trim(), out var custom) && custom > 0)
            {
                selectedMinutes = custom;
            }

            if (selectedMinutes > 0)
            {
                ViewModel.StartSleepTimer(selectedMinutes);
            }
            else if (ViewModel.IsSleepTimerActive && selectedMinutes == 0)
            {
                ViewModel.StopSleepTimer();
            }
        };

        await dialog.ShowAsync();
        UpdateSleepTimerDisplays();
    }

    private void SleepTimerCancelButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.StopSleepTimer();
        UpdateSleepTimerDisplays();
    }

    /// <summary>
    /// Планирует выключение компьютера (shutdown /s /t 0).
    /// </summary>
    private bool TryShutdownPc()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/s /t 0",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось запустить shutdown.exe для выключения ПК.");
            return false;
        }
    }

    /// <summary>
    /// Обновляет индикаторы таймера сна в обеих панелях (оконной и полноэкранной).
    /// </summary>
    private void UpdateSleepTimerDisplays()
    {
        var isActive = ViewModel.IsSleepTimerActive;
        var remainingText = ViewModel.SleepTimerRemainingText;

        WindowedSleepTimerPanel.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        WindowedSleepTimerText.Text = remainingText ?? string.Empty;

        OverlaySleepTimerPanel.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        OverlaySleepTimerText.Text = remainingText ?? string.Empty;
    }

    // ===================== Поверх всех окон / мини-плеер =====================

    private bool _panelsHiddenForMini;

    /// <summary>
    /// Ctrl+T / кнопка: окно поверх всех окон без смены размера и панелей —
    /// в отличие от мини-плеера, который плюс к этому сжимает окно до 480×300.
    /// </summary>
    private void ToggleAlwaysOnTop()
    {
        var window = MainWindow.Instance;
        if (window == null)
        {
            return;
        }

        window.SetAlwaysOnTop(!window.IsAlwaysOnTop);
        UpdateAlwaysOnTopButtons();
    }

    private void ToggleAlwaysOnTop(object sender, RoutedEventArgs e) => ToggleAlwaysOnTop();

    /// <summary>Синхронизирует тултипы обеих кнопок с состоянием окна.</summary>
    private void UpdateAlwaysOnTopButtons()
    {
        var window = MainWindow.Instance;
        if (window == null)
        {
            return;
        }

        var tooltip = window.IsAlwaysOnTop
            ? L.T("Poverkh_Vsekh_Okon_Vykl")
            : L.T("Poverkh_Vsekh_Okon_Vkl");
        ToolTipService.SetToolTip(VideoOverlayAlwaysOnTopButton, tooltip);
        ToolTipService.SetToolTip(OverlayAlwaysOnTopButton, tooltip);

        var opacity = window.IsAlwaysOnTop ? 1.0 : 0.55;
        VideoOverlayAlwaysOnTopButton.Opacity = opacity;
        OverlayAlwaysOnTopButton.Opacity = opacity;
    }

    /// <summary>
    /// Ctrl+M: компактное always-on-top окно только с видео; панели
    /// (список каналов, EPG) скрываются и возвращаются при выходе из режима.
    /// </summary>
    private void ToggleMiniPlayer()
    {
        MainWindow.Instance!.ToggleMiniPlayer();
        var mini = MainWindow.Instance.IsMiniPlayer;

        if (mini && !_panelsHiddenForMini)
        {
            _panelsHiddenForMini = true;
            ChannelListPanel.Visibility = Visibility.Collapsed;
            ChannelListColumn.MinWidth = 0;
            ChannelListColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            ViewModel.IsEpgVisible = false;
            EpgPanelBorder.Visibility = Visibility.Collapsed;
        }
        else if (!mini && _panelsHiddenForMini)
        {
            _panelsHiddenForMini = false;
            // Локальный файл (карточка «Видео»): панель каналов скрыта
            // навсегда — восстанавливать колонку нечего.
            if (_localVideoFile == null)
            {
                ChannelListColumn.MinWidth = 240;
                ChannelListColumn.Width = new GridLength(
                    Math.Max(240, ViewModel.AppSettings.ChannelListWidth), GridUnitType.Pixel);
                SplitterColumn.Width = GridLength.Auto;
                ChannelListPanel.Visibility = Visibility.Visible;
            }
        }

        // Смена размера окна с видео: DComp-остров может продолжать рисовать
        // по старым координатам — пересобираем компоновку (как при fullscreen).
        ForceVideoRelayout();
        UpdateAlwaysOnTopButtons();
    }

    // ===================== Избранные каналы =====================

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChannelViewModel channel })
        {
            return;
        }

        ViewModel.ToggleFavoriteCommand.Execute(channel);
    }

    // ===================== Напоминания о передачах =====================

    private void ReminderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: EPGEntry entry })
        {
            return;
        }

        ViewModel.ToggleReminderCommand.Execute(entry);
    }

    /// <summary>
    /// Тост Windows (CommunityToolkit.WinUI.Notifications).
    /// </summary>
    private void ShowReminderToast(Models.ProgramReminder reminder)
    {
        try
        {
            new CommunityToolkit.WinUI.Notifications.ToastContentBuilder()
                .AddText(string.Format(L.T("Skoro_V_Efire_0"), reminder.ProgramName))
                .AddText(string.Format(L.T("Nachalo_V_0"), reminder.ChannelName, $"{reminder.StartTime:HH:mm}"))
                .Show();
        }
        catch (Exception ex)
        {
            if (!_toastFailureLogged)
            {
                _toastFailureLogged = true;
                _logger.LogError(ex,
                    "Показ тоста-напоминания не удался (последующие ошибки до перезапуска не логируются).");
            }
        }
    }

    // ===================== Запись передач и каналов =====================

    private void ScheduleRecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: EPGEntry entry })
        {
            return;
        }

        ViewModel.ToggleScheduleRecordCommand.Execute(entry);
    }

    /// <summary>Синхронизирует вид обеих кнопок записи с состоянием сервиса.</summary>
    private void UpdateRecordButtons()
    {
        var active = ViewModel.Recording.IsRecordingStream(ViewModel.SelectedChannel?.StreamUrl);
        var currentPath = ViewModel.Recording.Active
            .FirstOrDefault(r => r.StreamUrl == ViewModel.SelectedChannel?.StreamUrl)?.OutputPath;

        VideoOverlayRecordButton.Content = active ? AppIcons.StopSquare(13) : AppIcons.RecordDot(14);
        ToolTipService.SetToolTip(VideoOverlayRecordButton, active
            ? string.Format(L.T("Ostanovit_Zapis_0"), currentPath, currentPath)
            : L.T("Zapisat_Kanal_Lbl"));

        OverlayRecordButton.Content = active ? AppIcons.StopSquare(17) : AppIcons.RecordDot(18);
        ToolTipService.SetToolTip(OverlayRecordButton, active
            ? L.T("Ostanovit_Zapis")
            : L.T("Zapisat_Kanal_Lbl"));
    }

    // ===================== Ошибка потока =====================

    private void ShowStreamError(string message)
    {
        StreamErrorText.Text = message;
        StreamErrorCard.Visibility = Visibility.Visible;
    }
}
