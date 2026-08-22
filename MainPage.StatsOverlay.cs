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

        sb.AppendLine(L.T($"Канал: {channel}", $"Channel: {channel}"));

        if (d == null || d.SystemSourceFallback)
        {
            sb.Append(L.T(
                "Источник: системный (FFmpeg не открывал поток)",
                "Source: system (FFmpeg did not open the stream)"));
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
            FFmpegInteropX.DecoderEngine.SystemDecoder => L.T("системный", "system"),
            FFmpegInteropX.DecoderEngine.FFmpegSoftwareDecoder => "FFmpeg (CPU)",
            _ => "—"
        };
        var hw = d.HardwareStatus switch
        {
            FFmpegInteropX.HardwareDecoderStatus.Available => L.T("доступен", "available"),
            FFmpegInteropX.HardwareDecoderStatus.NotAvailable => L.T("недоступен", "not available"),
            _ => "n/a"
        };

        sb.AppendLine(L.T(
            $"Видео: {(video.Count > 0 ? string.Join(", ", video) : "—")}",
            $"Video: {(video.Count > 0 ? string.Join(", ", video) : "—")}"));
        sb.AppendLine(L.T(
            $"Аудио: {(audio.Count > 0 ? string.Join(", ", audio) : "—")}",
            $"Audio: {(audio.Count > 0 ? string.Join(", ", audio) : "—")}"));
        sb.AppendLine(L.T(
            $"Декодер: {decoder} · аппаратный: {hw}",
            $"Decoder: {decoder} · HW: {hw}"));
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
            speedLine = L.T(
                $"Скорость потока: {FormatBitrate((long)measuredBps)} (изм.)",
                $"Stream speed: {FormatBitrate((long)measuredBps)} (meas.)");
        }
        else
        {
            speedLine = L.T(
                $"Скорость потока: {FormatBitrate(d.DownloadBitrate)} (оценка)",
                $"Stream speed: {FormatBitrate(d.DownloadBitrate)} (est.)");
        }

        sb.AppendLine(L.T(
            $"Буфер: {d.ReadAheadSeconds} c / {d.ReadAheadBytes / 1024 / 1024} МБ · простои: {_bufferingStallCount}",
            $"Buffer: {d.ReadAheadSeconds}s / {d.ReadAheadBytes / 1024 / 1024} MB · stalls: {_bufferingStallCount}"));
        sb.AppendLine(speedLine);

        // Живая строка: тикает каждую секунду — видно, что оверлей обновляется.
        var session = DateTime.UtcNow - _channelSessionStartUtc;
        sb.Append(L.T(
            $"Сессия канала: {(int)session.TotalHours:00}:{session.Minutes:00}:{session.Seconds:00}",
            $"Channel session: {(int)session.TotalHours:00}:{session.Minutes:00}:{session.Seconds:00}"));

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
