namespace IptvPlayer.Services;

using System;
using System.Collections.Generic;
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
        if (resumeFrom > 0)
        {
            msg.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
        }

        using var resp = await Http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
        // 200 after a resume request means the CDN ignored Range — start over
        if (resumeFrom > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            resumeFrom = 0;
        }

        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength + (resumeFrom > 0 ? resumeFrom : null);
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
}
