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
        /// </summary>
        public static string? GetFilters(string? mode) => mode switch
        {
            // Лёгкая резкость: ланцош-масштаб сам по себе мылит картинку.
            Sharp => "unsharp=5:5:0.8:5:5:0.0",
            // Чистка компресс-артефактов типичного IPTV-битрейта + резкость.
            Denoise => "hqdn3d=3:2:6:4,unsharp=5:5:0.6",
            // SD-каналы (576i/480i): edge-directed апскейл x2, затем чистка
            // и умеренная резкость. xbr требует чётных размеров входа.
            SdUpscale => "xbr=2,hqdn3d=2:1:4:3,unsharp=5:5:0.5",
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
