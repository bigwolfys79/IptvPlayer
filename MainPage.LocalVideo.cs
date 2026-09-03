using System;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer;

/// <summary>
/// Воспроизведение локальных видеофайлов (карточка «Видео» на хабе):
/// файл превращается в «канал» и играется тем же конвейером, что и VOD
/// портала — StartPlaybackAsync(isVod: true). Позиция досмотра хранится
/// в VodResumeStore под именем файла; в историю каналов и EPG файл не
/// попадает.
/// </summary>
public sealed partial class MainPage
{
    private LocalVideoFile? _localVideoFile;

    private async Task PlayLocalVideoFileAsync(LocalVideoFile file)
    {
        try
        {
            var channel = LocalVideoFileService.CreateChannel(file);
            ViewModel.SelectedChannel = channel;

            // Если файл уже смотрели и он не досмотрен до конца — предлагаем
            // продолжить (тот же диалог, что у VOD портала). Позиция хранится
            // под ключом «file::путь», отдельно от портала.
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
