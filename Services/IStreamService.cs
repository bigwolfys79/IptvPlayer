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
        int VodReadAheadSeconds);

    public interface IStreamService
    {
        Task<MediaPlayer> CreatePlayerAsync(string streamUrl, PlaybackConfig config, bool isVod = false);
        Task<StreamInfo> GetStreamInfoAsync(string streamUrl);

        /// <summary>
        /// Применяет нормализацию громкости к уже играющему плееру
        /// (переключение режима в настройках).
        /// </summary>
        void ApplyAudioFilters(MediaPlayer? player, string? mode);

        /// <summary>
        /// Снимок параметров потока, открытого последним CreatePlayerAsync
        /// (для оверлея статистики Ctrl+J). Null — пока ничего не открыто.
        /// </summary>
        PlaybackDiagnostics? CurrentDiagnostics { get; }

        /// <summary>
        /// Обновляет скорость загрузки потока. Вызывается каждую секунду.
        /// </summary>
        void UpdateDownloadSpeed(MediaPlayer? player);
    }
}
