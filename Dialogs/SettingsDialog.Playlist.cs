using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace IptvPlayer.Dialogs;

/// <summary>
/// Плейлист и источники EPG: добавление плейлиста с фоновой загрузкой EPG,
/// кэш плейлиста и двухшаговый сброс источников.
/// </summary>
public sealed partial class SettingsDialog
{
    // ===================== Источники EPG =====================

    private void AddEpgSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var url = EpgUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        EpgSources.Add(new EPGSource { Url = url, IsEnabled = true });
        EpgUrlBox.Text = string.Empty;
    }

    private void RemoveEpgSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EPGSource source })
        {
            EpgSources.Remove(source);
        }
    }

    // ===================== Плейлист =====================

    private async void AddPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        var url = PlaylistUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url))
        {
            SetPlaylistStatus(L.T("Введите URL плейлиста.", "Enter a playlist URL."));
            return;
        }

        AddPlaylistButton.IsEnabled = false;
        SetPlaylistStatus(L.T("Загрузка и разбор плейлиста...", "Loading and parsing playlist..."));

        try
        {
            var parsedChannels = await _m3uParserService.ParseFromUrlAsync(url);

            if (parsedChannels.Count == 0)
            {
                SetPlaylistStatus("В плейлисте не найдено ни одного канала. Проверьте формат файла.");
                return;
            }

            foreach (var channel in parsedChannels)
            {
                channel.Id = _viewModel.Channels.Count + 1;
                _viewModel.Channels.Add(channel);

                // Канал добавляется не только в ViewModel.Channels, но и в
                // ChannelRepository — из него EPGService берёт список каналов
                // (и по нему ищет TvgId для сопоставления с XMLTV).
                await _channelRepository.AddChannelAsync(channel);
            }

            _viewModel.UpdateChannelCountText();
            _viewModel.RefreshGroups();
            _viewModel.FilterChannels();

            // EpgViewModel работает по СВОЕЙ копии списка каналов — без этого
            // добавленные через диалог каналы не попадали ни в пересчёт
            // текущей передачи, ни в минутный таймер, и программа/иконки
            // появлялись только после перезапуска приложения.
            _viewModel.EpgViewModel.SetChannels(_viewModel.Channels.ToList());

            // Первый запуск: до добавления плейлиста SelectedChannel — заглушка
            // из конструктора MainPageViewModel (Id=0, без имени и потока), и
            // EPG-панель, забиндженная на SelectedChannel.EPGEntries, оставалась
            // пустой («Нет данных») даже ПОСЛЕ полной загрузки EPG — пока
            // пользователь сам не кликнет канал. Выбираем канал сразу, как это
            // делает InitializeAsync при старте без last-watched: без
            // автозапуска видео — первая настройка не должна включать поток
            // сама, клик по каналу всё равно запускает воспроизведение.
            if (string.IsNullOrEmpty(_viewModel.SelectedChannel?.StreamUrl) &&
                _viewModel.Channels.Count > 0)
            {
                _viewModel.SelectedChannel = _viewModel.Channels[0];
            }

            // Фоновая загрузка EPG для новых каналов (при первой настройке —
            // первое скачивание XMLTV, десятки секунд). Статус и обработка
            // ошибок — внутри LoadEpgInBackgroundAsync.
            _ = LoadEpgInBackgroundAsync(parsedChannels.Count);

            // Запоминаем URL сразу, чтобы при следующем старте подтянуть тот
            // же плейлист. Обновляем ОБЕ копии настроек — каноническую
            // (ViewModel.AppSettings) и локальную (_currentSettings).
            _currentSettings.PlaylistUrl = url;
            _viewModel.AppSettings.PlaylistUrl = url;
            await _settingsService.SaveAsync(_currentSettings);

            // Кэш плейлиста — при следующем запуске не перекачивать.
            await SavePlaylistCacheAsync(parsedChannels);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось загрузить плейлист {Url}.", url);
            SetPlaylistStatus($"Не удалось загрузить плейлист: {ex.Message}");
        }
        finally
        {
            AddPlaylistButton.IsEnabled = true;
        }
    }

    private void SetPlaylistStatus(string text)
    {
        PlaylistStatusText.Text = text;
        PlaylistStatusText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Фоновая загрузка EPG после добавления плейлиста (при первой настройке —
    /// первое скачивание XMLTV, десятки секунд). Раньше вызов LoadEPGAsync был
    /// «голым» fire-and-forget: исключение из него уходило в
    /// UnobservedTaskException, индикатор гас, а программы так и не появлялись —
    /// и ни в логе, ни в UI не было видно, почему. Теперь прогресс и итог видны
    /// в статусе диалога (пока он открыт), ошибка — в статусе и в логе.
    /// Тяжёлая работа (скачивание/парсинг XMLTV) внутри EpgViewModel.LoadEPGAsync
    /// идёт в пуле потоков и пачками с Task.Yield — UI-поток не блокируется.
    /// </summary>
    private async Task LoadEpgInBackgroundAsync(int addedChannels)
    {
        try
        {
            SetPlaylistStatus(L.T(
                $"Добавлено каналов: {addedChannels}. Загружается программа передач (EPG)...",
                $"Added channels: {addedChannels}. Downloading programme guide (EPG)..."));
            await _viewModel.EpgViewModel.LoadEPGAsync();
            SetPlaylistStatus(L.T(
                $"Добавлено каналов: {addedChannels}. Программа передач загружена.",
                $"Added channels: {addedChannels}. Programme guide loaded."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Фоновая загрузка EPG после добавления плейлиста.");
            SetPlaylistStatus(L.T(
                $"Добавлено каналов: {addedChannels}. EPG не загрузился: {ex.Message}",
                $"Added channels: {addedChannels}. EPG failed: {ex.Message}"));
        }
    }

    private Task SavePlaylistCacheAsync(List<ChannelViewModel> channels)
    {
        var cache = new PlaylistCache
        {
            SavedAtUtc = DateTime.UtcNow,
            Channels = channels.Select(c => new CachedChannel
            {
                Name = c.Name,
                StreamUrl = c.StreamUrl,
                LogoUrl = c.LogoUrl,
                Group = c.Group,
                TvgId = c.TvgId,
                CatchupDays = c.CatchupDays
            }).ToList()
        };

        return _playlistCacheService.SaveAsync(_viewModel.AppSettings.ActivePlaylistId, cache);
    }

    // ===================== Сброс =====================

    // «Сбросить» удаляет источники EPG и плейлист. Второй ContentDialog из
    // настроек показать нельзя, поэтому двухшаговое подтверждение: первое
    // нажатие «взводит» кнопку, второе — сбрасывает.
    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_resetArmed)
        {
            _resetArmed = true;
            ResetButton.Content = L.T("Точно сбросить?", "Really reset?");
            SetPlaylistStatus("Повторное нажатие удалит все источники EPG и плейлист (каналы пропадут).");
            return;
        }

        _resetArmed = false;
        ResetButton.Content = L.T("Сбросить", "Reset");
        ResetButton.IsEnabled = false;

        try
        {
            EpgSources.Clear();
            _viewModel.AppSettings.EpgSources = new List<EPGSource>();
            _viewModel.AppSettings.PlaylistUrl = null;
            await _settingsService.SaveAsync(_viewModel.AppSettings);

            // Пустой кэш плейлиста: иначе при следующем запуске каналы
            // вернулись бы из локального кэша, минуя удалённый источник.
            await _playlistCacheService.SaveAsync(_viewModel.AppSettings.ActivePlaylistId, new PlaylistCache
            {
                SavedAtUtc = DateTime.UtcNow,
                Channels = new List<CachedChannel>()
            });

            // Немедленно убираем каналы из интерфейса и останавливаем
            // воспроизведение, если что-то играло.
            _viewModel.Player.Stop();
            _viewModel.SelectedChannel = null;
            _viewModel.Channels.Clear();
            _viewModel.EpgViewModel.SetChannels(new List<ChannelViewModel>());
            _viewModel.UpdateChannelCountText();
            _viewModel.RefreshGroups();
            _viewModel.FilterChannels();

            PlaylistUrlBox.Text = string.Empty;
            SetPlaylistStatus("Источники EPG и плейлист удалены.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Сброс источников: не удалось выполнить сброс.");
            SetPlaylistStatus("Не удалось выполнить сброс (см. лог).");
        }
        finally
        {
            ResetButton.IsEnabled = true;
        }
    }
}
