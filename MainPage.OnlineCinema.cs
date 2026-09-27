using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer;


public sealed partial class MainPage : Page
{
    private DispatcherTimer? _cinemaCollectTimer;

    // Background catalog loading interval comes from the settings dialog
    private TimeSpan CinemaCollectInterval =>
        TimeSpan.FromSeconds(Math.Clamp(ViewModel.AppSettings.OnlineCinemaCollectIntervalSeconds, 30, 7200));

    // While a film from the online cinema is playing, collect catalog pages in
    // the background so later browsing needs less fetching
    private void EnsureCinemaCollectTimer()
    {
        if (_cinemaCollectTimer != null)
        {
            return;
        }

        _cinemaCollectTimer = new DispatcherTimer { Interval = CinemaCollectInterval };
        _cinemaCollectTimer.Tick += (_, _) => OnCinemaCollectTick();
        _cinemaCollectTimer.Start();
    }

    private void StopCinemaCollectTimer()
    {
        _cinemaCollectTimer?.Stop();
        _cinemaCollectTimer = null;
    }

    private void OnCinemaCollectTick()
    {
        // Pick up interval changes from the settings dialog without a restart
        if (_cinemaCollectTimer != null)
        {
            _cinemaCollectTimer.Interval = CinemaCollectInterval;
        }

        var playlist = ViewModel.AppSettings.Playlists
            .FirstOrDefault(p => p.Id == ViewModel.AppSettings.ActivePlaylistId);
        if (playlist == null || !playlist.IsOnlineCinema ||
            !ViewModel.AppSettings.OnlineCinemaEnabled)
        {
            return;
        }

        if (!Player.IsVodPlaying)
        {
            return;
        }

        _ = ViewModel.OnlineCinemaBackgroundCollectAsync();
    }

    // Online cinema: instant open from the catalog DB; the first time a full
    // sync runs under the loading overlay, later opens refresh page 1 of every
    // category in the background
    private async Task<List<ChannelViewModel>> LoadOnlineCinemaAsync(PlaylistSource playlist, CancellationToken ct)
    {
        ViewModel.ClearPortalInfo();
        ViewModel.SetVodSource(true);
        ViewModel.SetOnlineCinemaSource(true);
        ViewModel.SyncCatalogSorts();
        EnsureCinemaCollectTimer();

        if (!ViewModel.AppSettings.OnlineCinemaEnabled)
        {
            ViewModel.PlaylistLoadingText = L.T("OnlineCinema_Otklyuchen");
            _logger.LogInformation("Онлайн-кинотеатр: источник отключён в настройках — загрузка пропущена.");
            return new List<ChannelViewModel>();
        }

        var channels = await ViewModel.LoadOnlineCinemaFromDbAsync();
        if (channels.Count == 0)
        {
            ViewModel.PlaylistLoadingText = L.T("OnlineCinema_Zagruzka_Kataloga");
            await ViewModel.InitialSyncOnlineCinemaAsync();
            channels = await ViewModel.LoadOnlineCinemaFromDbAsync();
        }
        else
        {
            _ = ViewModel.RefreshOnlineCinemaInBackgroundAsync();
        }

        foreach (var channel in channels)
        {
            channel.IsVodCatalogItem = true;
        }

        return channels;
    }

    // Download the currently playing online-cinema item: pick a folder, then
    // queue a new row in the downloads menu (multiple downloads run in
    // parallel and survive film/playlist switches)
    private async void VodDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Player.IsVodPlaying || string.IsNullOrEmpty(Player.VodChannel?.PageUrl))
        {
            return;
        }

        var folder = await PickDownloadFolderAsync();
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        var error = await Player.StartOnlineCinemaDownloadAsync(folder);
        if (error != null)
        {
            ShowActionToast(error);
            return;
        }

        await new Dialogs.DownloadsDialog(ViewModel.DownloadManager, ViewModel).ShowAsync(Content.XamlRoot);
    }

    // Sort combo: applies the site sort for the current category
    private async void OnlineCinemaSortFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedOnlineCinemaSort is { } option)
        {
            await ViewModel.ApplyOnlineCinemaSortAsync(option.Value);
        }
    }

    // FolderPicker; remember the choice for the next time
    private async Task<string?> PickDownloadFolderAsync()
    {
        var service = App.Services.GetRequiredService<Services.LocalVideoFileService>();
        var path = await service.PickFolderAsync();
        if (!string.IsNullOrEmpty(path) && path != ViewModel.AppSettings.OnlineCinemaDownloadFolder)
        {
            ViewModel.AppSettings.OnlineCinemaDownloadFolder = path;
            await _settingsService.SaveAsync(ViewModel.AppSettings);
        }

        return path;
    }
}
