using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer;

/// <summary>
/// Navigation and playlist switching logic.
/// </summary>
public sealed partial class MainPage : Page
{
    /// <summary>
    /// Кнопка "Назад" в Hub Page: возврат на главный экран.
    /// </summary>
    private void BackToHubButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private Task<bool> OnVodResumePromptRequested(string title, TimeSpan position)
    {
        var resumeCompletion = new TaskCompletionSource<bool>();
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dialog = new ThemedContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = L.T("Prodolzhit_Prosmotr"),
                    Content = new TextBlock
                    {
                        Text = string.Format(L.T("0_Vy_Ostanovilis_Na_1_Prodolzhit"), title, PlayerViewModel.FormatArchiveTime(position.TotalSeconds), title, PlayerViewModel.FormatArchiveTime(position.TotalSeconds)),
                        TextWrapping = TextWrapping.Wrap
                    },
                    PrimaryButtonText = L.T("Prodolzhit"),
                    CloseButtonText = L.T("Smotret_Snachala")
                };
                resumeCompletion.SetResult(await dialog.ShowAsync() == ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Диалог возобновления VOD не показался.");
                resumeCompletion.SetResult(false);
            }
        });
        return resumeCompletion.Task;
    }

    private async void PosterViewToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.AppSettings.ChannelListPosterView = !ViewModel.AppSettings.ChannelListPosterView;
        await _settingsService.SaveAsync(ViewModel.AppSettings);
        ApplyChannelViewMode();
    }

    /// <summary>
    /// Наполняет подменю «Сменить плейлист» в меню настроек: активный отмечен
    /// галочкой (ToggleMenuFlyoutItem в стиле остальных пунктов), клик по
    /// пункту переключает плейлист. Подменю прячется, когда плейлист один
    /// (переключать нечего). Вызывается при старте и после изменения списка
    /// плейлистов в диалоге настроек.
    /// </summary>
    private void UpdatePlaylistMenu()
    {
        var playlists = ViewModel.AppSettings.Playlists;
        SwitchPlaylistSubMenu.Items.Clear();
        foreach (var playlist in playlists)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = playlist.Name,
                IsChecked = playlist.Id == ViewModel.AppSettings.ActivePlaylistId,
                Tag = playlist
            };
            item.Click += SwitchPlaylistMenuItem_Click;
            SwitchPlaylistSubMenu.Items.Add(item);
        }

        SwitchPlaylistSubMenu.Visibility = playlists.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SwitchPlaylistMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: PlaylistSource playlist } &&
            playlist.Id != ViewModel.AppSettings.ActivePlaylistId)
        {
            await SwitchPlaylistAsync(playlist);
        }
    }

    /// <summary>
    /// Переключение активного плейлиста: останавливает воспроизведение, чистит
    /// каналы предыдущего плейлиста (репозиторий + список + EPG) и наполняет
    /// их каналами нового — той же логикой кэша/обновления, что и при старте.
    /// Автопродолжение последнего канала не запускается: переключение —
    /// осознанное действие, видео включится кликом по каналу.
    /// </summary>
    private async Task SwitchPlaylistAsync(PlaylistSource playlist)
    {
        if (_activePlaylist?.Id == playlist.Id)
        {
            return;
        }

        try
        {
            ViewModel.Player.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Остановка плеера при переключении плейлиста.");
        }

        _activePlaylist = playlist;
        ViewModel.AppSettings.ActivePlaylistId = playlist.Id;
        await _settingsService.SaveAsync(ViewModel.AppSettings);

        // Сбрасываем серверные фильтры при смене плейлиста.
        if (!playlist.IsPortal)
        {
            ViewModel.ClearPortalInfo();
        }

        _playlistLoadCts?.Cancel();
        _playlistLoadCts = new System.Threading.CancellationTokenSource();
        var channels = await LoadPlaylistChannelsAsync(playlist, _playlistLoadCts.Token);

        var channelId = 1;
        foreach (var channel in channels)
        {
            channel.Id = channelId++;
        }

        await _channelRepository.Clear();
        foreach (var channel in channels)
        {
            await _channelRepository.AddChannelAsync(channel);
        }

        ViewModel.Channels = new ObservableCollection<ChannelViewModel>(channels);

        // Избранное глобальное (по имени канала) — переживает переключение.
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

        var lastWatched = string.IsNullOrWhiteSpace(playlist.LastWatchedChannel)
            ? null
            : ViewModel.Channels.FirstOrDefault(c =>
                string.Equals(c.Name, playlist.LastWatchedChannel, StringComparison.OrdinalIgnoreCase));
        ViewModel.SelectedChannel = lastWatched ?? ViewModel.Channels.FirstOrDefault();

        UpdatePlaylistMenu();

        // EPG у каждого плейлиста свой (источники XMLTV в PlaylistSource):
        // после смены набора каналов программы перечитываются с источников
        // нового плейлиста фоном, без очистки дискового кэша общего фида.
        _ = LoadEpgAfterPlaylistSwitchAsync();
    }

    private async Task LoadEpgAfterPlaylistSwitchAsync()
    {
        try
        {
            await ViewModel.EpgViewModel.ReloadForPlaylistAsync();
            ViewModel.ApplyReminderFlags();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Перезагрузка EPG после переключения плейлиста.");
        }
    }

    /// <summary>
    /// Автопродолжение последнего канала при запуске: то же, что клик по
    /// каналу, но без блокировки InitializeAsync и без обновления
    /// LastWatchedChannel (он и есть этот канал).
    /// </summary>
    private async Task ContinueWatchingAsync(ChannelViewModel channel)
    {
        try
        {
            // Родительский контроль: автопродолжение заблокированной группы
            // тоже требует PIN — тихо включать такой канал нельзя.
            if (!await ViewModel.CanPlayChannelAsync(channel))
            {
                return;
            }
            await PlayLiveAsync(channel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Автопродолжение последнего канала ({Name}).", channel.Name);
        }
    }

    /// <summary>
    /// VOD resume из Hub Page: находим канал по названию, разрешаем
    /// эпизоды portal-сервиса и запускаем нужный эпизод без диалога.
    /// </summary>
    private async Task ResumeVodFromHubAsync(string title, int episodeIndex)
    {
        try
        {
            var channel = ViewModel.Channels.FirstOrDefault(c =>
                string.Equals(c.Name, title, StringComparison.OrdinalIgnoreCase));
            if (channel == null)
            {
                return;
            }

            ViewModel.SelectedChannel = channel;
            _ = ScrollSelectedChannelIntoViewAsync();

            if (!string.IsNullOrWhiteSpace(channel.PortalRequest))
            {
                var playlist = ViewModel.AppSettings.Playlists
                    .FirstOrDefault(p => p.Id == ViewModel.AppSettings.ActivePlaylistId);
                if (playlist == null)
                {
                    return;
                }

                // Если есть прямая ссылка из каталога — играем её сразу
                // (как делает PlayChannelAsync), без flick-запроса.
                if (!string.IsNullOrWhiteSpace(channel.StreamUrl))
                {
                    var catalogResume = ViewModel.GetSavedVodPosition(title, episodeIndex);
                    await Player.StartPlaybackAsync(channel, channel.StreamUrl, archiveEntry: null,
                        isVod: true, resumePosition: catalogResume);
                    return;
                }

                Player.IsBuffering = true;
                PortalFlickResult flick;
                try
                {
                    flick = await _videoPortalService.ResolveEpisodesAsync(playlist, channel.PortalRequest);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ResumeVodFromHub: flick не удался для «{Title}».", title);
                    return;
                }
                finally
                {
                    Player.IsBuffering = false;
                }

                var epIndex = episodeIndex >= 0 && episodeIndex < flick.Episodes.Count
                    ? episodeIndex : 0;
                var episode = flick.Episodes[epIndex];
                var savedPos = ViewModel.GetSavedVodPosition(title, epIndex);

                var preferred = ViewModel.AppSettings.PreferredQuality > 0
                    ? ViewModel.AppSettings.PreferredQuality + "p" : "Авто";
                var quality = episode.Variants.Count > 0 ? preferred : null;

                await Player.StartPlaybackAsync(channel, episode.StreamUrl, archiveEntry: null,
                    isVod: true, vodVariants: episode.Variants, vodQuality: quality,
                    resumePosition: savedPos,
                    vodEpisodes: flick.Episodes, vodEpisodeIndex: epIndex);
            }
            else
            {
                await ViewModel.PlayChannelAsync(channel, interactive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VOD resume из Hub Page для «{Title}».", title);
        }
    }

    /// <summary>
    /// Прокручивает список каналов к восстановленному при старте каналу.
    /// </summary>
    private async Task ScrollSelectedChannelIntoViewAsync()
    {
        await ScrollChannelIntoViewAsync(ChannelsListView);
    }

    /// <summary>
    /// Прокрутка полноэкранного списка каналов к текущему.
    /// </summary>
    private async Task ScrollOverlayChannelIntoViewAsync()
    {
        var channel = ViewModel.SelectedChannel;
        if (channel == null)
        {
            return;
        }

        await Task.Yield();

        var waited = 0;
        for (var i = 0; i < 15 && OverlayChannelsListView.ItemsPanelRoot == null; i++)
        {
            await Task.Delay(100);
            waited += 100;
        }

        Serilog.Log.Debug(
            "OverlayList: прокрутка к «{Channel}» — панель списка {PanelState} (ожидали {Waited} мс), Items {Count}",
            channel.Name,
            OverlayChannelsListView.ItemsPanelRoot == null ? "НЕ готова" : "готова",
            waited,
            OverlayChannelsListView.Items.Count);

        OverlayChannelsListView.ScrollIntoView(channel, ScrollIntoViewAlignment.Leading);
    }

    /// <summary>
    /// Прокрутка списка каналов к выбранному — как оконного, так и полноэкранного
    /// оверлея.
    /// </summary>
    private async Task ScrollChannelIntoViewAsync(ListView list)
    {
        if (ViewModel.SelectedChannel == null)
        {
            return;
        }

        await Task.Yield();
        await Task.Delay(150);

        list.ScrollIntoView(ViewModel.SelectedChannel);
        await Task.Delay(50);

        try
        {
            if (list.ContainerFromItem(ViewModel.SelectedChannel) is not FrameworkElement container)
            {
                return;
            }

            var scrollViewer = FindDescendant<ScrollViewer>(list);
            if (scrollViewer == null)
            {
                return;
            }

            var content = (UIElement)scrollViewer.Content;
            var itemTop = container.TransformToVisual(content)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            scrollViewer.ChangeView(null, Math.Max(0, itemTop - 4), null, disableAnimation: true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Information(ex, "Центрирование выбранного канала в списке.");
        }
    }
}
