using System;
using System.Collections.Generic;

namespace IptvPlayer.Services
{
    /// <summary>
    /// Пресеты улучшения картинки («апскейлер») — цепочки видео-фильтров
    /// FFmpeg, применяемые через VideoConfig.FFmpegVideoFilters при открытии
    /// потока или живьём через FFmpegMediaSource.SetFFmpegVideoFilters
    /// (тот же механизм, что и нормализация громкости).
    ///
    /// Состав пресетов подобран по фильтрам, реально присутствующим в бандл-
    /// сборке FFmpeg 8.1.2 (avfilter-11.dll): unsharp, hqdn3d, xbr и
    /// качественные флаги масштабирования. Нейросетевого dnn_processing в
    /// этой сборке нет (требуется FFmpeg с libopenvino/libtensorflow).
    /// </summary>
    public static class VideoUpscaler
    {
        public const string Off = "Off";
        public const string Sharp = "Sharp";
        public const string Denoise = "Denoise";
        public const string SdUpscale = "SdUpscale";

        /// <summary>Все режимы в порядке следования в меню кнопки.</summary>
        public static readonly IReadOnlyList<string> AllModes = new[]
        {
            Off, Sharp, Denoise, SdUpscale
        };

        /// <summary>
        /// Цепочка видео-фильтров для режима; null — фильтры не нужны.
        ///
        /// ВАЖНО: все фильтры должны сохранять размер кадра. FFmpegInteropX
        /// фиксирует разрешение выхода по дескриптору потока при открытии и
        /// любой кадр иного размера (например, после xbr=2) сжимает обратно
        /// к исходному (UncompressedVideoSampleProvider.InitializeScalerIfRequired)
        /// — эффект масштабирования стирается. Усиление резкости/чистка
        /// работают честно.
        /// </summary>
        public static string? GetFilters(string? mode) => mode switch
        {
            // Заметная резкость: amount 1.5 хорошо виден на SD и HD.
            Sharp => "unsharp=5:5:1.5:5:5:0.3",
            // Чистка компресс-артефактов типичного IPTV-битрейта + резкость.
            Denoise => "hqdn3d=4:3:6:4.5,unsharp=5:5:1.0:5:5:0.3",
            // SD-каналы (576i/480i): артефакты апскейла в эфире заметнее
            // всего как «грязь» — чистим и вытягиваем резкость.
            SdUpscale => "hqdn3d=3:2:6:4,unsharp=5:5:1.8:5:5:0.4",
            _ => null
        };

        /// <summary>
        /// Валидирует значение настроек: неизвестное значение трактуется как Off.
        /// </summary>
        public static string Normalize(string? mode)
        {
            foreach (var m in AllModes)
            {
                if (string.Equals(m, mode, StringComparison.OrdinalIgnoreCase))
                {
                    return m;
                }
            }
            return Off;
        }
    }
}
