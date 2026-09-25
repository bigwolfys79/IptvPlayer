using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;

public interface IOnlineCinemaStreamResolver
{
    // Resolves a fresh stream URL from the film page at play time — CDN links
    // carry a date-bound token and expire, so they are never cached
    Task<OnlineCinemaStream?> ResolveAsync(string pageUrl, CancellationToken ct = default);
}


// kinogo.online: film page -> trusted CDP clicks on the embed player iframes
// (the site hosts several providers) -> intercepted m3u8 responses. Mirrors the
// proven reference parser flow; the m3u8 hit itself is provider-agnostic (any
// response whose body mentions m3u8, or the classic {"file": ...} api JSON).
// Everything runs in the hidden browser — nothing is ever shown to the user.
public class OnlineCinemaStreamResolver : IOnlineCinemaStreamResolver
{
    private static readonly TimeSpan ClickInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(60);

    private readonly OnlineCinemaBrowserService _browser;
    private readonly ILogger _logger;

    public OnlineCinemaStreamResolver(OnlineCinemaBrowserService browser, ILogger<OnlineCinemaStreamResolver> logger)
    {
        _browser = browser;
        _logger = logger;
    }

    public async Task<OnlineCinemaStream?> ResolveAsync(string pageUrl, CancellationToken ct = default)
    {
        if (!_browser.IsInitialized)
        {
            await _browser.InitializeAsync();
        }

        _browser.ClearPlaylistResponses();
        if (!await _browser.NavigateAsync(pageUrl, ct))
        {
            _logger.LogWarning(
                "Онлайн-кинотеатр: страница фильма не открылась (или сайт требует проверку): {Url}", pageUrl);
            return null;
        }

        return await ResolveCoreAsync(pageUrl, ct);
    }

    private async Task<OnlineCinemaStream?> ResolveCoreAsync(string pageUrl, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TotalTimeout;
        var clicked = 0;
        var lastSeenSrc = string.Empty;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            // Candidates: every embed iframe on the page (ads and YouTube excluded).
            // The site's own click handler loads the embed — exactly like the
            // parser: we never assign iframe src ourselves
            var candidates = await _browser.RunScriptJsonAsync(KinogoSite.IframeCandidatesJs, ct);
            if (candidates.ValueKind == JsonValueKind.Array)
            {
                foreach (var rect in candidates.EnumerateArray())
                {
                    if (rect.ValueKind != JsonValueKind.Object || !rect.TryGetProperty("x", out var cx))
                    {
                        continue;
                    }

                    var src = rect.TryGetProperty("src", out var s) ? s.GetString() ?? string.Empty : string.Empty;
                    if (src != lastSeenSrc)
                    {
                        lastSeenSrc = src;
                        _logger.LogInformation(
                            "Онлайн-кинотеатр: клик по плееру {Src} ({W}x{H}).",
                            src,
                            rect.TryGetProperty("w", out var w) ? w.GetDouble() : 0,
                            rect.TryGetProperty("h", out var h) ? h.GetDouble() : 0);
                    }

                    await _browser.ClickAtAsync(cx.GetDouble(), rect.GetProperty("y").GetDouble());
                    clicked++;
                    var hit = await _browser.WaitForStreamHitAsync(ClickInterval, ct);
                    if (hit != null && ExtractStream(hit) is { } stream)
                    {
                        await FillVariantsAsync(stream);
                        _logger.LogInformation(
                            "Онлайн-кинотеатр: поток получен для {Url} (кликов: {Clicks}), вариантов {Variants}: {Hit}",
                            pageUrl, clicked, stream.Variants.Count, hit.Url.Length > 100 ? hit.Url[..100] : hit.Url);
                        return stream;
                    }
                }
            }
            else if (DateTime.UtcNow > deadline - TimeSpan.FromSeconds(15))
            {
                // No stream player on the page — maybe a YouTube trailer only
                var yt = await _browser.RunScriptAsync(KinogoSite.HasYouTubeOnlyJs, ct);
                if (!string.IsNullOrWhiteSpace(yt) && yt != "null")
                {
                    _logger.LogInformation("Онлайн-кинотеатр: на странице только YouTube-трейлер.");
                    return new OnlineCinemaStream { Label = "трейлер (YouTube)", Url = yt };
                }
            }
        }

        _logger.LogWarning(
            "Онлайн-кинотеатр: поток не получен за {Seconds} с: {Url} (кликов: {Clicks}).",
            TotalTimeout.TotalSeconds, pageUrl, clicked);
        return null;
    }

    // Fetches the master playlist and parses its renditions so the quality
    // picker has options; failure keeps the plain link (media playlist case)
    private static async Task FillVariantsAsync(OnlineCinemaStream stream)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var master = await http.GetStringAsync(stream.Url);
            var variants = StreamService.ParseHlsMasterVariants(master, new Uri(stream.Url));
            if (variants.Count == 0)
            {
                return;
            }

            foreach (var kv in variants)
            {
                stream.Variants[kv.Key] = kv.Value;
            }

            stream.Variants["Авто"] = stream.Url;
        }
        catch (Exception)
        {
            // Master fetch is best-effort — playback falls back to the plain link
        }
    }

    // Provider-agnostic extraction: JSON {"file": ...} (string or array entries)
    // wins — it carries the master playlist (quality variants build from it);
    // otherwise the hit URL itself when the body is an m3u8 playlist
    private static OnlineCinemaStream? ExtractStream(OnlineCinemaBrowserService.StreamHit hit)
    {
        try
        {
            using var doc = JsonDocument.Parse(hit.Body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    if (el.TryGetProperty("file", out var f) && f.GetString() is { Length: > 0 } file)
                    {
                        return new OnlineCinemaStream { Url = file };
                    }
                }
            }
            else if (root.TryGetProperty("file", out var fileEl) &&
                     fileEl.ValueKind == JsonValueKind.String &&
                     fileEl.GetString() is { Length: > 0 } single)
            {
                return new OnlineCinemaStream { Url = single };
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the m3u8 body check
        }

        if (hit.Body.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase) ||
            hit.Body.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return new OnlineCinemaStream { Url = hit.Url };
        }

        return null;
    }
}
