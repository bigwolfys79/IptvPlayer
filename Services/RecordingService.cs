using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;


public sealed class ActiveRecording
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string ChannelName { get; init; } = string.Empty;
    public string StreamUrl { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public int? DurationSec { get; init; }
}


public sealed class RecordingService
{

    public const int MaxConcurrent = 3;

    private readonly ILogger<RecordingService> _logger;
    private readonly Dictionary<Guid, (Process Proc, ActiveRecording Info)> _active = new();
    private readonly object _gate = new();

    public RecordingService(ILogger<RecordingService> logger)
    {
        _logger = logger;
    }


    public event EventHandler? RecordingsChanged;


    public bool IsActive => PruneExited() > 0;


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


    public bool IsRecordingChannel(string channelName)
    {
        lock (_gate)
        {
            PruneExited();
            return _active.Values.Any(v =>
                string.Equals(v.Info.ChannelName, channelName, StringComparison.OrdinalIgnoreCase));
        }
    }


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

        // Hygiene: odd URLs would just confuse ffmpeg, reject early
        if (streamUrl.IndexOfAny(['"', '\'', '\r', '\n']) >= 0)
        {
            _logger.LogWarning(
                "StreamUrl содержит недопустимые символы (кавычки/переводы строк) — запись не запущена.");
            return null;
        }

        // Strict URL whitelist: only stream schemes reach ffmpeg argv
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var streamUri) ||
            streamUri.Scheme is not ("http" or "https" or "rtmp" or "rtmps" or "rtsp" or "rtp" or "udp" or "mms"))
        {
            _logger.LogWarning("StreamUrl не является поддерживаемым URL ({Scheme}) — запись не запущена.", streamUri?.Scheme);
            return null;
        }
        var streamUrlArg = streamUri.AbsoluteUri;

        try
        {
            var dir = string.IsNullOrWhiteSpace(recordsFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "IptvPlayer")
                : recordsFolder;
            Directory.CreateDirectory(dir);

            var safe = SanitizeFileName(fileNameBase);
            var path = Path.Combine(dir, $"{safe} {DateTime.Now:yyyy-MM-dd HHmmss}.ts");

            var psi = new ProcessStartInfo(exe)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true
            };
            // ArgumentList handles quoting/escaping; no shell, no string concat
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(streamUrlArg);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("mpegts");
            psi.ArgumentList.Add(path);
            if (durationSec is > 0)
            {
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add(durationSec.Value.ToString(CultureInfo.InvariantCulture));
            }

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
                ((Process)s!).Dispose();
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


        System.Threading.Tasks.Task.Run(() => TryStopProcess(process));

        RecordingsChanged?.Invoke(this, EventArgs.Empty);
    }


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


    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "IptvPlayer");


    private int PruneExited()
    {
        var dead = _active.Where(kv => kv.Value.Proc.HasExited).Select(kv => kv.Key).ToList();
        foreach (var id in dead)
        {
            var proc = _active[id].Proc;
            _active.Remove(id);
            proc.Dispose();
        }
        return _active.Count;
    }


    private static string SanitizeFileName(string name)
    {
        var s = Regex.Replace(name, @"[\\/:*?""<>|]", "_").Trim();
        return string.IsNullOrEmpty(s) ? "recording" : s;
    }
}
