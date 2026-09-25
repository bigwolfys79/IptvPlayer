using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer;


public sealed partial class MainPage : Page
{
    // Online cinema: instant open from the catalog DB; the first time a full
    // sync runs under the loading overlay, later opens refresh page 1 of every
    // category in the background
    private async Task<List<ChannelViewModel>> LoadOnlineCinemaAsync(PlaylistSource playlist, CancellationToken ct)
    {
        ViewModel.ClearPortalInfo();
        ViewModel.SetVodSource(true);
        ViewModel.SetOnlineCinemaSource(true);

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
