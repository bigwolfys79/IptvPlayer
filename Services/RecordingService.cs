using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;

/// <summary>Идёт сейчас запись с этого URL потока.</summary>
public sealed class ActiveRecording
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string ChannelName { get; init; } = string.Empty;
    public string StreamUrl { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public int? DurationSec { get; init; }
}

/// <summary>
/// Запись IPTV-потока в файл силой ffmpeg.exe, который лежит рядом с
/// приложением (tools/ffmpeg.exe в проекте; DLL те же, что и для
/// FFmpegInteropX — совпадают мажорные версии). Копирование потока без
/// перекодирования (-c copy) в MPEG-TS: нагрузка на CPU ~нулевая, а файл
/// остаётся воспроизводимым даже при резком обрыве записи.
///
/// Поддерживает несколько параллельных записей (лимит MaxConcurrent —
/// это IPTV: слишком много одновременных сессий одного провайдера
/// нередко режется его лимитами). Останов = Kill процесса:
/// неполный TS валиден. Папка записей настраивается; null —
/// «Видео\IptvPlayer».
/// </summary>
public sealed class RecordingService
{
    /// <summary>Максимальное число одновременных записей (лимит сессий провайдера).</summary>
    public const int MaxConcurrent = 3;

    private readonly ILogger<RecordingService> _logger;
    private readonly Dictionary<Guid, (Process Proc, ActiveRecording Info)> _active = new();
    private readonly object _gate = new();

    public RecordingService(ILogger<RecordingService> logger)
    {
        _logger = logger;
    }

    /// <summary>Меняется при старте/завершении/остановке любой записи (для UI).</summary>
    public event EventHandler? RecordingsChanged;

    /// <summary>Идёт ли хоть одна запись прямо сейчас.</summary>
    public bool IsActive => PruneExited() > 0;

    /// <summary>Текущие записи (для списка в UI).</summary>
    public IReadOnlyList<ActiveRecording> Active
    {
        get
        {
            lock (_gate)
            {
                PruneExited();
                return _active.Values.Select(v => v.Info).ToList();
            }
        }
    }

    /// <summary>Идёт ли запись этого потока (кнопка REC на конкретном канале).</summary>
    public bool IsRecordingStream(string? streamUrl)
    {
        if (string.IsNullOrEmpty(streamUrl))
        {
            return false;
        }
        lock (_gate)
        {
            PruneExited();
            return _active.Values.Any(v => v.Info.StreamUrl == streamUrl);
        }
    }

    /// <summary>Канал сейчас пишется (по имени, для расписаний).</summary>
    public bool IsRecordingChannel(string channelName)
    {
        lock (_gate)
        {
            PruneExited();
            return _active.Values.Any(v =>
                string.Equals(v.Info.ChannelName, channelName, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Запускает запись потока. durationSec = null — до ручной остановки.
    /// recordsFolder = null — «Видео\IptvPlayer». Возвращает описание записи
    /// или null, если ffmpeg недоступен или достигнут лимит параллельных записей.
    /// </summary>
    public ActiveRecording? Start(
        string streamUrl,
        string fileNameBase,
        string channelName,
        int? durationSec,
        string? recordsFolder = null)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (!File.Exists(exe))
        {
            _logger.LogWarning("ffmpeg.exe не найден рядом с приложением — запись недоступна.");
            return null;
        }

        lock (_gate)
        {
            PruneExited();
            if (_active.Count >= MaxConcurrent)
            {
                _logger.LogWarning(
                    "Достигнут лимит одновременных записей ({Limit}) — старт отменён.", MaxConcurrent);
                return null;
            }
        }

        try
        {
            var dir = string.IsNullOrWhiteSpace(recordsFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "IptvPlayer")
                : recordsFolder;
            Directory.CreateDirectory(dir);

            var safe = SanitizeFileName(fileNameBase);
            var path = Path.Combine(dir, $"{safe} {DateTime.Now:yyyy-MM-dd HHmmss}.ts");

            // -nostdin убран: stdin перенаправлен, и аккуратная остановка
            // идёт посылкой 'q' (ffmpeg допишет заголовки TS и выйдет сам),
            // Kill остаётся запасным вариантом.
            var args = "-hide_banner -loglevel error -y " +
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
                RedirectStandardOutput = true,
                RedirectStandardInput = true
            };

            var process = Process.Start(psi);
            if (process == null)
            {
                return null;
            }

            var info = new ActiveRecording
            {
                ChannelName = channelName,
                StreamUrl = streamUrl,
                OutputPath = path,
                StartedAt = DateTime.Now,
                DurationSec = durationSec
            };

            lock (_gate)
            {
                _active[info.Id] = (process, info);
            }

            // Стоки обязаны вычитываться, иначе заполненный пайп блокирует ffmpeg.
            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) _logger.LogInformation("ffmpeg: {Line}", e.Data);
            };
            process.OutputDataReceived += (s, e) => { };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            process.EnableRaisingEvents = true;
            process.Exited += (s, e) =>
            {
                var exitedPath = path;
                lock (_gate)
                {
                    _active.Remove(info.Id);
                }
                _logger.LogInformation("Запись завершена (код {ExitCode}): {Path}",
                    ((Process)s!).ExitCode, exitedPath);
                RecordingsChanged?.Invoke(this, EventArgs.Empty);
            };

            _logger.LogInformation("Начата запись: {Path}{Duration}",
                path, durationSec is > 0 ? $" ({durationSec} c)" : " (до остановки)");
            RecordingsChanged?.Invoke(this, EventArgs.Empty);
            return info;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось запустить ffmpeg.");
            return null;
        }
    }

    /// <summary>Останавливает запись по id (файл остаётся валидным TS).</summary>
    public void Stop(Guid id)
    {
        Process? process;
        lock (_gate)
        {
            if (!_active.TryGetValue(id, out var entry))
            {
                return;
            }
            process = entry.Proc;
            _active.Remove(id);
        }

        // Ожидание выхода ffmpeg (до 3 с) — в фоне, UI не замирает.
        System.Threading.Tasks.Task.Run(() => TryStopProcess(process));

        RecordingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Останавливает все записи (закрытие приложения).</summary>
    public void StopAll()
    {
        List<Process> processes;
        lock (_gate)
        {
            processes = _active.Values.Select(v => v.Proc).ToList();
            _active.Clear();
        }

        foreach (var process in processes)
        {
            TryStopProcess(process);
        }

        if (processes.Count > 0)
        {
            RecordingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Аккуратная остановка: 'q' в stdin — ffmpeg сам дописывает заголовки
    /// TS и завершается (ждём до 3 с); не вышло — Kill (неполный TS всё
    /// равно валиден, это запасной путь).
    /// </summary>
    private void TryStopProcess(Process? process)
    {
        if (process is not { HasExited: false })
        {
            return;
        }

        try
        {
            process.StandardInput.Write('q');
            process.StandardInput.Flush();
            if (process.WaitForExit(3000))
            {
                return;
            }
            _logger.LogWarning("ffmpeg не завершился по 'q' за 3 с — принудительная остановка.");
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Аккуратная остановка ffmpeg не удалась — пробуем Kill.");
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex2)
            {
                _logger.LogError(ex2, "Не удалось остановить процесс записи.");
            }
        }
    }

    /// <summary>Папка записей по умолчанию (для кнопки «Открыть папку»).</summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "IptvPlayer");

    /// <summary>
    /// Внутри lock(_gate): убирает самозавершившиеся процессы и возвращает
    /// число живых записей.
    /// </summary>
    private int PruneExited()
    {
        var dead = _active.Where(kv => kv.Value.Proc.HasExited).Select(kv => kv.Key).ToList();
        foreach (var id in dead)
        {
            _active.Remove(id);
        }
        return _active.Count;
    }

    /// <summary>Имя файла из названия канала/передачи без запрещённых символов.</summary>
    private static string SanitizeFileName(string name)
    {
        var s = Regex.Replace(name, @"[\\/:*?""<>|]", "_").Trim();
        return string.IsNullOrEmpty(s) ? "recording" : s;
    }
}
