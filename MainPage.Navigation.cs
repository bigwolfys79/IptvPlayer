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


public sealed partial class MainPage : Page
{


    // Back to hub screen
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
                resumeCompletion.SetResult(await DialogQueue.ShowAsync(dialog) == ContentDialogResult.Primary);
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


    // Rebuild playlist switch submenu
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


    private async Task SwitchPlaylistAsync(PlaylistSource playlist)
    {
        if (_activePlaylist?.Id == playlist.Id)
        {
            return;
        }

        await ApplyPlaylistAsync(playlist);
    }


    // Reload active playlist
    public Task ReloadActivePlaylistAsync() =>
        _activePlaylist is { } playlist ? ApplyPlaylistAsync(playlist) : Task.CompletedTask;

    // Load channels of the given playlist
    private async Task ApplyPlaylistAsync(PlaylistSource playlist)
    {
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


        if (!playlist.IsPortal)
        {
            ViewModel.ClearPortalInfo();
        }

        _playlistLoadGeneration++;
        _playlistLoadCts?.Cancel();
        _playlistLoadCts = new System.Threading.CancellationTokenSource();
        var generation = _playlistLoadGeneration;
        var channels = await LoadPlaylistChannelsWithOverlayAsync(playlist, _playlistLoadCts.Token);
        if (generation != _playlistLoadGeneration)
        {
            return;
        }
        channels = await ApplyChannelOverridesAsync(channels);
        if (generation != _playlistLoadGeneration)
        {
            return;
        }

        var channelId = 1;
        foreach (var channel in channels)
        {
            channel.Id = channelId++;
        }

        await _channelRepository.Clear();
        await _channelRepository.AddChannelsAsync(channels);

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

        var lastWatched = string.IsNullOrWhiteSpace(playlist.LastWatchedChannel)
            ? null
            : ViewModel.Channels.FirstOrDefault(c =>
                string.Equals(c.Name, playlist.LastWatchedChannel, StringComparison.OrdinalIgnoreCase));
        ViewModel.SelectedChannel = lastWatched ?? ViewModel.Channels.FirstOrDefault();

        UpdatePlaylistMenu();

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


    // Auto-resume last channel on startup
    private async Task ContinueWatchingAsync(ChannelViewModel channel)
    {
        try
        {


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


    // Resume VOD from hub screen
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


    private async Task ScrollSelectedChannelIntoViewAsync()
    {
        await ScrollChannelIntoViewAsync(ChannelsListView);
    }


    private async Task ScrollOverlayChannelIntoViewAsync()
    {
        var channel = ViewModel.SelectedChannel;
        if (channel == null)
        {
            return;
        }

        await Task.Yield();

        var waited = await WaitForItemsPanelAsync(OverlayChannelsListView, TimeSpan.FromSeconds(1.5));

        Serilog.Log.Debug(
            "OverlayList: прокрутка к «{Channel}» — панель списка {PanelState} (ожидали {Waited} мс), Items {Count}",
            channel.Name,
            OverlayChannelsListView.ItemsPanelRoot == null ? "НЕ готова" : "готова",
            waited,
            OverlayChannelsListView.Items.Count);

        OverlayChannelsListView.ScrollIntoView(channel, ScrollIntoViewAlignment.Leading);
    }

    // Poll ItemsPanelRoot with a dispatcher timer (no thread sleeps)
    private static async Task<int> WaitForItemsPanelAsync(ItemsControl list, TimeSpan timeout)
    {
        if (list.ItemsPanelRoot != null)
        {
            return 0;
        }

        var waited = 0;
        var stepMs = 50;
        var tcs = new TaskCompletionSource();
        var timer = list.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(stepMs);
        timer.Tick += (s, e) =>
        {
            waited += stepMs;
            if (list.ItemsPanelRoot != null || waited >= timeout.TotalMilliseconds)
            {
                timer.Stop();
                tcs.TrySetResult();
            }
        };
        timer.Start();
        await tcs.Task;
        return waited;
    }


    private async Task ScrollChannelIntoViewAsync(ListView list)
    {
        if (ViewModel.SelectedChannel == null)
        {
            return;
        }

        await Task.Yield();
        await WaitForItemsPanelAsync(list, TimeSpan.FromSeconds(1.5));

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
