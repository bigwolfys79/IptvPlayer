using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    private DispatcherTimer? _cinemaCollectTimer;

    // While a film from the online cinema is playing, collect catalog pages in
    // the background (~every 2 minutes) so later browsing needs less fetching
    private void EnsureCinemaCollectTimer()
    {
        if (_cinemaCollectTimer != null)
        {
            return;
        }

        _cinemaCollectTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
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
}
