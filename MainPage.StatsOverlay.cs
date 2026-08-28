using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.Controls;
using IptvPlayer.ViewModels;
using Windows.System;
using Windows.UI.Core;
// Windows.Media.Playback.MediaPlayer конфликтует по имени с x:Name="MediaPlayer"
// (MediaPlayerElement) в разметке, поэтому в коде тип всегда указывается
// с полным неймспейсом: Windows.Media.Playback.MediaPlayer.

namespace IptvPlayer;

/// <summary>
/// Оверлей статистики потока (Ctrl+J).
/// Вынесено из MainPage.xaml.cs (MVVM-этап 3: разбиение code-behind по зонам).
/// </summary>
public sealed partial class MainPage
{
    // Простои воспроизведения за текущий канал: инкремент по
    // BufferingStarted плеера (подписка в конструкторе), сброс при смене
    // плеера. Показывается в последней строке оверлея.
    private int _bufferingStallCount;

    // Начало просмотра текущего канала — для живой строки «Сессия»: она
    // тикает каждую секунду и сразу видно, что оверлей обновляется (кодеки
    // и буфер сами по себе меняются редко).
    private DateTime _channelSessionStartUtc = DateTime.UtcNow;

    private void ToggleStatsOverlay() =>
        SetStatsOverlayVisible(StatsOverlay.Visibility != Visibility.Visible);

    /// <summary>
    /// Показывает/прячет оверлей статистики. Единая точка для Ctrl+J,
    /// тумблера в настройках и старта приложения; состояние запоминается
    /// (persist = false — на старте, чтобы не перезаписывать файл настроек
    /// тем же значением).
    /// </summary>
    private void SetStatsOverlayVisible(bool show, bool persist = true)
    {
        StatsOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (persist)
        {
            ViewModel.AppSettings.StatsOverlayVisible = show;
            _settingsSaveDebounceTimer.Stop();
            _settingsSaveDebounceTimer.Start();
        }

        if (show)
        {
            UpdateStatsOverlay();
        }
    }

    /// <summary>
    /// Пересобирает текст статистики: статические параметры потока — из
    /// снимка StreamService.CurrentDiagnostics, живые (заполнение буфера,
    /// простои) — из сессии/событий текущего плеера. Вызывается секундным
    /// тиком и по BufferingStarted; скрытый оверлей — тихий no-op.
    /// </summary>
    private void UpdateStatsOverlay()
    {
        if (StatsOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        var channel = ViewModel.SelectedChannel?.Name ?? "—";
        var d = _streamService.CurrentDiagnostics;
        var sb = new StringBuilder();

        sb.AppendLine(string.Format(L.T("Kanal_0"), channel, channel));

        if (d == null || d.SystemSourceFallback)
        {
            sb.Append(L.T("Istochnik_Sistemnyy_FFmpeg_Ne_Otkryval_Potok"));
            StatsText.Text = sb.ToString();
            return;
        }

        // Обновляем скорость загрузки каждую секунду
        _streamService.UpdateDownloadSpeed(Player.Player);

        var video = new List<string>();
        if (!string.IsNullOrEmpty(d.VideoCodec))
        {
            video.Add(d.VideoCodec!);
        }
        if (d.VideoWidth > 0 && d.VideoHeight > 0)
        {
            video.Add($"{d.VideoWidth}×{d.VideoHeight}");
        }
        if (d.FramesPerSecond > 0)
        {
            video.Add($"{d.FramesPerSecond:F0} fps");
        }
        if (d.VideoBitrate > 0)
        {
            video.Add($"{d.VideoBitrate / 1000} kbps");
        }
        if (d.IsHdr)
        {
            video.Add("HDR");
        }

        var audio = new List<string>();
        if (!string.IsNullOrEmpty(d.AudioCodec))
        {
            audio.Add(d.AudioCodec!);
        }
        if (!string.IsNullOrEmpty(d.AudioChannelLayout))
        {
            audio.Add(d.AudioChannelLayout!);
        }
        else if (d.AudioChannels > 0)
        {
            audio.Add($"{d.AudioChannels} ch");
        }
        if (d.AudioSampleRate > 0)
        {
            audio.Add($"{d.AudioSampleRate / 1000.0:F1} kHz");
        }
        if (d.AudioBitrate > 0)
        {
            audio.Add($"{d.AudioBitrate / 1000} kbps");
        }

        // Фактический декодер видео — как его выбрал FFmpegInteropX (в
        // аппаратном режиме Automatic возможен откат на CPU) + статус
        // аппаратного декодера на этой машине.
        var decoder = d.VideoDecoderEngine switch
        {
            FFmpegInteropX.DecoderEngine.FFmpegD3D11HardwareDecoder => "FFmpeg D3D11 (GPU)",
            FFmpegInteropX.DecoderEngine.SystemDecoder => L.T("Sistemnyy"),
            FFmpegInteropX.DecoderEngine.FFmpegSoftwareDecoder => "FFmpeg (CPU)",
            _ => "—"
        };
        var hw = d.HardwareStatus switch
        {
            FFmpegInteropX.HardwareDecoderStatus.Available => L.T("Dostupen"),
            FFmpegInteropX.HardwareDecoderStatus.NotAvailable => L.T("Nedostupen"),
            _ => "n/a"
        };

        var videoList = video.Count > 0 ? string.Join(", ", video) : "—";
        var audioList = audio.Count > 0 ? string.Join(", ", audio) : "—";
        sb.AppendLine(string.Format(L.T("Stat_VideoCodecs"), videoList));
        sb.AppendLine(string.Format(L.T("Stat_AudioCodecs"), audioList));
        sb.AppendLine(string.Format(L.T("Dekoder_0_Apparatnyy_1"), decoder, hw, decoder, hw));
        // BufferingProgress сессии у живых MediaStreamSource-потоков всегда 0
        // (реальный read-ahead буфер живёт внутри FFmpegInteropX и наружу не
        // отдаётся) — «заполнение 0%» только сбивало с толку, показываем
        // честное: глубину из настроек + счётчик простоев.

        // Скорость потока: измеренная (счётчик байт чтения процесса — FFmpeg
        // качает поток сокетами этого же процесса, см. ProcessSpeedMonitor)
        // или оценка по метаданным/разрешению, пока измерения нет (старт
        // канала, чужая загрузка вроде EPG — на её время замер заморожен).
        var measuredBps = _speedMonitor.Sample();
        string speedLine;
        if (measuredBps is > 0)
        {
            speedLine = string.Format(L.T("Skorost_Potoka_0_Izm"), FormatBitrate((long)measuredBps), FormatBitrate((long)measuredBps));
        }
        else
        {
            speedLine = string.Format(L.T("Skorost_Potoka_0_Otsenka"), FormatBitrate(d.DownloadBitrate), FormatBitrate(d.DownloadBitrate));
        }

        sb.AppendLine(string.Format(L.T("Bufer_0_C_1_MB_Prostoi"), d.ReadAheadSeconds, d.ReadAheadBytes / 1024 / 1024, _bufferingStallCount, d.ReadAheadSeconds, d.ReadAheadBytes / 1024 / 1024, _bufferingStallCount));
        sb.AppendLine(speedLine);

        // Живая строка: тикает каждую секунду — видно, что оверлей обновляется.
        var session = DateTime.UtcNow - _channelSessionStartUtc;
        sb.Append(string.Format(L.T("Sessiya_Kanala_0_1_2"), $"{(int)session.TotalHours:00}", $"{session.Minutes:00}", $"{session.Seconds:00}", $"{(int)session.TotalHours:00}", $"{session.Minutes:00}", $"{session.Seconds:00}"));

        StatsText.Text = sb.ToString();
    }

    /// <summary>
    /// Форматирует битрейт в читаемый вид (kbps, Mbps, Gbps).
    /// </summary>
    private static string FormatBitrate(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
            return "—";

        if (bitsPerSecond < 1_000_000)
            return $"{bitsPerSecond / 1000.0:F1} kbps";

        if (bitsPerSecond < 1_000_000_000)
            return $"{bitsPerSecond / 1_000_000.0:F2} Mbps";

        return $"{bitsPerSecond / 1_000_000_000.0:F2} Gbps";
    }

}
