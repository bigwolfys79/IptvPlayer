using System;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer;


public sealed partial class MainPage
{
    private LocalVideoFile? _localVideoFile;

    private async Task PlayLocalVideoFileAsync(LocalVideoFile file)
    {
        try
        {
            var channel = LocalVideoFileService.CreateChannel(file);
            ViewModel.SelectedChannel = channel;

            var resume = await ViewModel.OfferLocalFileResumeAsync(file.Path, file.Title);

            await Player.StartPlaybackAsync(channel, channel.StreamUrl!, archiveEntry: null,
                isVod: true, resumePosition: resume);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Запуск локального файла «{File}» не удался.", file.Path);
            ViewModel.Player.StreamError = L.T("LocalVideo_Open_Failed");
        }
    }
}
