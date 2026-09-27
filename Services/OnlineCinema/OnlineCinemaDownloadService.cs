namespace IptvPlayer.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public interface IOnlineCinemaDownloadService
{
    // Streams the MP4 to targetPath (.part while running); resumes from an
    // existing .part when the server honours Range. Reports percent 0..100,
    // -1 while the total size is unknown
    Task DownloadAsync(string url, string targetPath, IProgress<int> progress, CancellationToken ct);

    // HLS variant (VOD): remuxes the stream into an MP4 via the bundled
    // ffmpeg.exe (-c copy). Restart-based — no segment-level resume
    Task DownloadHlsAsync(string hlsUrl, string targetPath, IProgress<int> progress, CancellationToken ct);
}


// Streaming downloader for the online-cinema direct MP4 links: the CDN answers
// anonymous GETs with Accept-Ranges support, so the file is written to
// <name>.part and appended on resume
public class OnlineCinemaDownloadService : IOnlineCinemaDownloadService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(100),
        MaxResponseContentBufferSize = 1 << 20
    };

    private readonly ILogger<OnlineCinemaDownloadService> _logger;

    public OnlineCinemaDownloadService(ILogger<OnlineCinemaDownloadService> logger)
    {
        _logger = logger;
    }

    public async Task DownloadAsync(string url, string targetPath, IProgress<int> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var partPath = targetPath + ".part";
        var resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.TryAddWithoutValidation("User-Agent", KinogoSite.UserAgent);
        // Range on every request (even from 0): the CDN then answers 206 with
        // Content-Range carrying the FULL file size — a plain 200 often has
        // no Content-Length, which left the progress at 0%
        msg.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);

        using var resp = await Http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
        // 200 after a resume request means the CDN ignored Range — start over
        if (resumeFrom > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            resumeFrom = 0;
        }

        resp.EnsureSuccessStatusCode();
        long? total = resp.StatusCode == System.Net.HttpStatusCode.PartialContent
            ? resp.Content.Headers.ContentRange?.Length
            : resp.Content.Headers.ContentLength;
        var reportAt = Environment.TickCount64;
        void Report(long received)
        {
            // Throttle progress callbacks to ~2/s — the UI toast is updated
            // through DispatcherQueue and must not be flooded
            var now = Environment.TickCount64;
            if (now - reportAt < 500)
            {
                return;
            }

            reportAt = now;
            progress.Report(total is > 0 ? (int)(received * 100 / total.Value) : -1);
        }

        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(partPath, resumeFrom > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            var received = resumeFrom;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                Report(received);
            }

            progress.Report(100);
        }
        catch (OperationCanceledException)
        {
            // Keep the .part for a later resume
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Онлайн-кинотеатр: скачивание оборвалось ({Received} байт) — {Url}.", resumeFrom, url);
            throw;
        }

        File.Move(partPath, targetPath, overwrite: true);
        _logger.LogInformation("Онлайн-кинотеатр: скачано {Path} ({Size} байт).", targetPath,
            new FileInfo(targetPath).Length);
    }

    // File name from title/context parts: strip path-invalid chars, collapse spaces
    public static string BuildFileName(IEnumerable<string> parts, string quality)
    {
        var name = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, ' ');
        }

        name = System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s+", " ");
        return $"{name} [{quality}].mp4";
    }

    // Remuxes a VOD HLS playlist (video + separate audio group) into a single
    // MP4 with the bundled ffmpeg.exe (-c copy — no re-encoding). Progress
    // comes from ffmpeg's -progress pipe:1 (out_time_us vs Duration)
    public async Task DownloadHlsAsync(string hlsUrl, string targetPath, IProgress<int> progress, CancellationToken ct)
    {
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (!File.Exists(ffmpeg))
        {
            throw new InvalidOperationException("ffmpeg.exe не найден рядом с приложением.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var partPath = targetPath + ".part";
        if (File.Exists(partPath))
        {
            File.Delete(partPath); // remux restarts from scratch — no partial resume
        }

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
        };
        foreach (var arg in new[]
        {
            "-y", "-hide_banner", "-loglevel", "info", "-stats_period", "1",
            "-user_agent", KinogoSite.UserAgent,
            "-i", hlsUrl,
            "-c", "copy",
            "-movflags", "+faststart",
            // The .part extension means ffmpeg can't guess the muxer — set it
            "-f", "mp4",
            "-progress", "pipe:1",
            partPath,
        })
        {
            psi.ArgumentList.Add(arg);
        }

        double? totalSec = null;
        double lastSec = 0;
        var reportAt = Environment.TickCount64;
        void Report(double seconds)
        {
            var now = Environment.TickCount64;
            if (now - reportAt < 500)
            {
                return;
            }

            reportAt = now;
            progress.Report(totalSec is > 0 ? (int)(seconds * 100 / totalSec.Value) : -1);
        }

        using var proc = new Process { StartInfo = psi };
        using var killReg = ct.Register(() =>
        {
            try { proc.Kill(true); } catch { }
        });
        proc.Start();

        // stderr carries the container "Duration: ..." line — the only total
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) != null)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        line, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                    if (m.Success)
                    {
                        totalSec = int.Parse(m.Groups[1].Value) * 3600
                            + int.Parse(m.Groups[2].Value) * 60
                            + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    }
                    else if (System.Text.RegularExpressions.Regex.IsMatch(
                        line, @"error|failed|Invalid|410", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        // Visible in the app log: 410/signature errors here
                        // explain most download failures
                        _logger.LogInformation("ffmpeg hls: {Line}", line);
                    }
                }
            }
            catch (Exception) { /* process exited */ }
        }, CancellationToken.None);

        // stdout carries -progress key=value blocks
        string? progressLine;
        try
        {
            while ((progressLine = await proc.StandardOutput.ReadLineAsync(ct)) != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    progressLine, @"out_time_us=(?<us>\d+)");
                if (!m.Success)
                {
                    continue;
                }

                lastSec = long.Parse(m.Groups["us"].Value) / 1_000_000.0;
                Report(lastSec);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ffmpeg progress stream ended.");
        }

        await proc.WaitForExitAsync(ct);
        if (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        if (proc.ExitCode != 0)
        {
            _logger.LogWarning("ffmpeg remux завершился с кодом {Code} — {Url}.", proc.ExitCode, hlsUrl);
            throw new InvalidOperationException($"ffmpeg exit code {proc.ExitCode}");
        }

        File.Move(partPath, targetPath, overwrite: true);
        progress.Report(100);
        _logger.LogInformation("Онлайн-кинотеатр: HLS скачан {Path} ({Size} байт).", targetPath,
            new FileInfo(targetPath).Length);
    }
}
