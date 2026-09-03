using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.Logging;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT;

namespace IptvPlayer.Services;

/// <summary>
/// Локальные видеофайлы (карточка «Видео» на хабе): выбор файла через
/// FileOpenPicker и сборка «канала» для плеера. Воспроизведение идёт тем
/// же конвейером FFmpegInteropX, что и VOD портала: file:///-URI →
/// StreamService.CreatePlayerAsync(isVod: true), поэтому seek/пауза/фулскрин
/// достаются бесплатно.
/// </summary>
public class LocalVideoFileService
{
    /// <summary>Расширения, предлагаемое в диалоге выбора.</summary>
    private static readonly string[] VideoExtensions =
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".ts",
        ".m2ts", ".wmv", ".m4v", ".mpg", ".mpeg", ".3gp"
    };

    /// <summary>Похоже ли имя файла на поддерживаемое видео (для аргумента запуска).</summary>
    public static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<LocalVideoFileService> _logger;

    public LocalVideoFileService(ILogger<LocalVideoFileService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Открывает диалог выбора видеофайла. Возвращает null, если пользователь
    /// отменил выбор. В WinUI 3 (unpackaged в том числе) пикеру нужен hwnd
    /// окна — передаётся через IInitializeWithWindow.
    /// </summary>
    public async Task<LocalVideoFile?> PickAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            ViewMode = PickerViewMode.List
        };
        foreach (var ext in VideoExtensions)
        {
            picker.FileTypeFilter.Add(ext);
        }

        if (MainWindow.Instance is { } window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            picker.As<IInitializeWithWindow>().Initialize(hwnd);
        }

        try
        {
            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return null;
            }

            return FromPath(file.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Выбор локального видеофайла не удался.");
            return null;
        }
    }

    /// <summary>Собирает модель из пути (например, для будущего drag&drop).</summary>
    public static LocalVideoFile FromPath(string path)
    {
        var title = System.IO.Path.GetFileNameWithoutExtension(path);
        return new LocalVideoFile(path, title);
    }

    /// <summary>
    /// «Канал» для плеера из локального файла: имя — заголовок, StreamUrl —
    /// «сырой» путь диска (E:\видео\файл.mpg). Протокол file: в FFmpeg НЕ
    /// декодирует URL-проценты, поэтому кириллица/пробелы в file:///-URI
    /// ломают открытие — avformat'у нужен сырой путь. Системный фолбэк
    /// (MediaSource.CreateFromUri(new Uri(path))) сам превращает путь в
    /// корректный file:///-URI. Id = -1: в списке каналов его нет, поиск
    /// по Id в UI обязан падать обратно на SelectedChannel.
    /// </summary>
    public static ChannelViewModel CreateChannel(LocalVideoFile file)
    {
        return new ChannelViewModel
        {
            Id = -1,
            Name = file.Title,
            StreamUrl = file.Path,
            IsLive = false,
            IsLocalFile = true
        };
    }

    [ComImport]
    [Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithWindow
    {
        void Initialize(IntPtr hwnd);
    }
}
