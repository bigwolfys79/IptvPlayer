using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Playback;
using IptvPlayer.Models;

namespace IptvPlayer.Services
{


    public record PlaybackConfig(
        string? DecoderMode,
        string? AudioNormalization,
        int AudioVolumeBoost,
        int ReadAheadSeconds,
        int VodReadAheadSeconds,
        bool DiagnosticProxy = false,
        string? VideoUpscaler = null,
        bool FrameServer = false);

    public interface IStreamService
    {
        Task<MediaPlayer> CreatePlayerAsync(string streamUrl, PlaybackConfig config, bool isVod = false, CancellationToken ct = default);

        void ReleasePlayer(MediaPlayer? player);


        double? ProxyMeasuredBitrate { get; }


        void ApplyAudioFilters(MediaPlayer? player, string? mode, bool allowLoudness = false, int boostPercent = 100);


        void ApplyVideoFilters(MediaPlayer? player, string? mode);


        PlaybackDiagnostics? CurrentDiagnostics { get; }


        Task<string> DiagnoseStreamUrl(string? streamUrl);

    }
}
