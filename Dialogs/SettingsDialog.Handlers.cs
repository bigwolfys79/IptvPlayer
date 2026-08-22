using System;
using System.Threading.Tasks;
using IptvPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace IptvPlayer.Dialogs;

/// <summary>
/// Обработчики контролов и сохранение по «Готово». Именованные (а не лямбды),
/// чтобы LoadAsync могла отписаться перед повторной загрузкой контролов после
/// импорта настроек.
/// </summary>
public sealed partial class SettingsDialog
{
    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Применяем сразу: статичные элементы страницы переведёт applyLanguage.
        L.SetLanguage(LanguageCombo.SelectedIndex == 1 ? "en" : "ru");
        _applyLanguage();
    }

    private void ThemeRadio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((RadioButtons)sender)?.SelectedItem is RadioButton { Tag: string tag })
        {
            _applyTheme(tag);
        }
    }

    private void BufferSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateBufferLabel();
    }

    private void FileLoggingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var enabled = FileLoggingToggle.IsOn;
        _currentSettings.FileLoggingEnabled = enabled;
        _viewModel.AppSettings.FileLoggingEnabled = enabled;
        App.SetFileLoggingEnabled(enabled);
    }

    private void StatsOverlayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var enabled = StatsOverlayToggle.IsOn;
        _currentSettings.StatsOverlayVisible = enabled;
        _viewModel.AppSettings.StatsOverlayVisible = enabled;
        _applyStatsOverlay(enabled);
    }

    // ===================== Сохранение («Готово») =====================

    private bool EpgSourcesChanged()
    {
        var current = EpgSources.Select(s => (s.Url, s.IsEnabled)).ToList();
        if (current.Count != _initialEpgSources.Count)
        {
            return true;
        }
        for (var i = 0; i < current.Count; i++)
        {
            if (current[i] != _initialEpgSources[i])
            {
                return true;
            }
        }
        return false;
    }

    private async Task RefreshEpgInBackgroundAsync()
    {
        try
        {
            await _viewModel.EpgViewModel.RefreshEPGAsync();
        }
        catch
        {
            // RefreshEPGAsync логирует ошибку сама и ретбросит исключение;
            // здесь ретбросить некому (fire-and-forget) — просто гасим.
        }
    }

    private async Task SaveSettingsAsync()
    {
        // Пишем в каноническую копию AppSettings, а не в загруженную при
        // открытии диалога: избранное/напоминания/последний канал могли
        // измениться после открытия — устаревшая копия затёрла бы их.
        _viewModel.AppSettings.EpgSources = EpgSources.ToList();

        if (PlaylistRefreshCombo.SelectedItem is ComboBoxItem playlistItem)
        {
            _viewModel.AppSettings.PlaylistRefreshDays = (int)(playlistItem.Tag ?? 1);
        }
        if (EpgRefreshCombo.SelectedItem is ComboBoxItem epgItem)
        {
            _viewModel.AppSettings.EpgRefreshDays = (int)(epgItem.Tag ?? 1);
        }

        // Режим декодирования и буфер применятся к следующему созданному
        // плееру — то есть при переключении канала.
        if (DecoderRadio.SelectedItem is RadioButton decoderItem && decoderItem.Tag is string decoderMode)
        {
            _viewModel.AppSettings.DecoderMode = decoderMode;
        }

        // Нормализация громкости — применяется к текущему каналу сразу.
        var audioNorm = (AudioNormRadio.SelectedItem as RadioButton)?.Tag as string;
        if (!string.IsNullOrEmpty(audioNorm))
        {
            _viewModel.AppSettings.AudioNormalization = audioNorm;
        }

        if (ReminderMinutesCombo.SelectedItem is ComboBoxItem reminderItem)
        {
            _viewModel.AppSettings.ReminderMinutes = (int)(reminderItem.Tag ?? 5);
        }

        if (SleepTimerActionCombo.SelectedItem is ComboBoxItem sleepActionItem &&
            sleepActionItem.Tag is string sleepAction)
        {
            _viewModel.AppSettings.SleepTimerAction = sleepAction;
        }

        if (ThemeRadio.SelectedItem is RadioButton { Tag: string theme })
        {
            _viewModel.AppSettings.Theme = theme;
        }
        _viewModel.AppSettings.Language = LanguageCombo.SelectedIndex == 1 ? "en" : "ru";
        _viewModel.AppSettings.ReadAheadSeconds = (int)Math.Clamp(BufferSlider.Value, 5, 120);
        _viewModel.AppSettings.FileLoggingEnabled = FileLoggingToggle.IsOn;
        _viewModel.AppSettings.StatsOverlayVisible = StatsOverlayToggle.IsOn;

        // Качество видео.
        if (QualityCombo.SelectedItem is ComboBoxItem qualityItem && qualityItem.Tag is int quality)
        {
            _viewModel.AppSettings.PreferredQuality = quality;
        }

        await _settingsService.SaveAsync(_viewModel.AppSettings);

        // Переключение аудио фильтров слышно сразу — фильтры заменяются в графе
        // играющего канала, без пересоздания плеера. Следующие каналы получат
        // их ещё при создании (StreamService.CreatePlayerAsync).
        _streamService.ApplyAudioFilters(_viewModel.Player.Player, audioNorm);

        if (!EpgSourcesChanged())
        {
            return;
        }

        // Список источников действительно поменялся — форсируем перезагрузку
        // EPG по новому набору (EPGService кэширует распарсенный EPG в памяти
        // на сессию, поэтому без явного Refresh новые источники не
        // подхватятся). Fire-and-forget: диалог закрывается сразу, перекачка
        // идёт фоном (тяжёлая часть — в пуле потоков внутри RefreshEPGAsync).
        _initialEpgSources = EpgSources.Select(s => (s.Url, s.IsEnabled)).ToList();
        _ = RefreshEpgInBackgroundAsync();
    }
}
