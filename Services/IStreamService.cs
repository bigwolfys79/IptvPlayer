using System.Threading.Tasks;
using Windows.Media.Playback;
using IptvPlayer.Models;

namespace IptvPlayer.Services
{
    /// <summary>
    /// Параметры потока для CreatePlayerAsync — передаются вызывающим
    /// кодом вместо чтения settings.json с диска.
    /// </summary>
    public record PlaybackConfig(
        string? DecoderMode,
        string? AudioNormalization,
        int ReadAheadSeconds,
        int VodReadAheadSeconds,
        bool DiagnosticProxy = false,
        string? VideoUpscaler = null);

    public interface IStreamService
    {
        Task<MediaPlayer> CreatePlayerAsync(string streamUrl, PlaybackConfig config, bool isVod = false);

        /// <summary>
        /// Реальная скорость потока в бит/с, измеренная диагностическим
        /// прокси (LocalStreamProxy), или null — прокси выключен/нет данных.
        /// </summary>
        double? ProxyMeasuredBitrate { get; }

        /// <summary>
        /// Применяет нормализацию громкости к уже играющему плееру
        /// (переключение режима в настройках).
        /// </summary>
        void ApplyAudioFilters(MediaPlayer? player, string? mode);

        /// <summary>
        /// Применяет пресет улучшения картинки к уже играющему плееру
        /// (кнопка «Качество картинки»). Для плееров без FFmpeg-источника
        /// ничего не делает.
        /// </summary>
        void ApplyVideoFilters(MediaPlayer? player, string? mode);

        /// <summary>
        /// Снимок параметров потока, открытого последним CreatePlayerAsync
        /// (для оверлея статистики Ctrl+J). Null — пока ничего не открыто.
        /// </summary>
        PlaybackDiagnostics? CurrentDiagnostics { get; }

        /// <summary>
        /// Диагностика URL потока: проверяет доступность и возвращает
        /// человекочитаемое описание проблемы.
        /// </summary>
        Task<string> DiagnoseStreamUrl(string? streamUrl);

    }
}
