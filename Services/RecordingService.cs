using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;

/// <summary>
/// Запись IPTV-потока в файл силой ffmpeg.exe, который лежит рядом с
/// приложением (tools/ffmpeg.exe в проекте; DLL те же, что и для
/// FFmpegInteropX — совпадают мажорные версии). Копирование потока без
/// перекодирования (-c copy) в MPEG-TS: нагрузка на CPU ~нулевая, а файл
/// остаётся воспроизводимым даже при резком обрыве записи.
///
/// Одна активная запись за раз (это IPTV: параллельные сессии одного
/// провайдера нередко режутся лимитами). Останов = Kill процесса:
/// неполный TS валиден.
/// </summary>
public sealed class RecordingService
{
    private readonly ILogger<RecordingService> _logger;
    private Process? _process;
    private string? _outputPath;

    public RecordingService(ILogger<RecordingService> logger)
    {
        _logger = logger;
    }

    /// <summary>Идет ли запись прямо сейчас.</summary>
    public bool IsActive => _process is { HasExited: false };

    /// <summary>Файл текущей/последней записи (для сообщений UI).</summary>
    public string? OutputPath => _outputPath;

    /// <summary>
    /// Запускает запись потока. durationSec = null — до ручной остановки.
    /// Возвращает путь к файлу или null, если ffmpeg недоступен/занят.
    /// </summary>
    public string? Start(string streamUrl, string fileNameBase, int? durationSec)
    {
        if (IsActive)
        {
            return null;
        }

        var exe = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (!File.Exists(exe))
        {
            _logger.LogWarning("ffmpeg.exe не найден рядом с приложением — запись недоступна.");
            return null;
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "IptvPlayer");
            Directory.CreateDirectory(dir);

            var safe = SanitizeFileName(fileNameBase);
            var path = Path.Combine(dir, $"{safe} {DateTime.Now:yyyy-MM-dd HHmmss}.ts");

            var args = "-hide_banner -loglevel error -nostdin -y " +
                       $"-i \"{streamUrl}\" -c copy -f mpegts \"{path}\"";
            if (durationSec is > 0)
            {
                args += $" -t {durationSec}";
            }

            var psi = new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            _process = Process.Start(psi);
            _outputPath = path;

            // Стоки обязаны вычитываться, иначе заполненный пайп блокирует ffmpeg.
            _process!.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) _logger.LogInformation("ffmpeg: {Line}", e.Data); };
            _process.OutputDataReceived += (s, e) => { };
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();
            _process.Exited += (s, e) => _logger.LogInformation(
                "Запись завершена (код {ExitCode}): {Path}",
                ((Process)s!).ExitCode, _outputPath);

            _logger.LogInformation("Начата запись: {Path}{Duration}",
                path, durationSec is > 0 ? $" ({durationSec} c)" : " (до остановки)");
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось запустить ffmpeg.");
            _process = null;
            _outputPath = null;
            return null;
        }
    }

    /// <summary>Останавливает текущую запись (файл остаётся валидным TS).</summary>
    public void Stop()
    {
        try
        {
            if (IsActive)
            {
                _process!.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось остановить запись.");
        }
    }

    /// <summary>Имя файла из названия канала/передачи без запрещённых символов.</summary>
    private static string SanitizeFileName(string name)
    {
        var s = Regex.Replace(name, @"[\\/:*?""<>|]", "_").Trim();
        return string.IsNullOrEmpty(s) ? "recording" : s;
    }
}
