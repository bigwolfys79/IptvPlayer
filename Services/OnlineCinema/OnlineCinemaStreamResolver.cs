using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
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


// Online-cinema stream resolution, fully browserless on the happy path:
// film page (same-origin WAF headers) -> embed iframe -> decoded
// "#2" playlist (CinemarDecoder) -> POST api/playlist/load -> master .m3u8.
// The hidden WebView2 (CDP clicks + browser-level m3u8 interception) remains
// the fallback for when the WAF blocks plain HTTP or the embed scheme changes.
public class OnlineCinemaStreamResolver : IOnlineCinemaStreamResolver
{
    private static readonly TimeSpan ClickInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(60);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly OnlineCinemaBrowserService _browser;
    private readonly ILogger _logger;

    public OnlineCinemaStreamResolver(OnlineCinemaBrowserService browser, ILogger<OnlineCinemaStreamResolver> logger)
    {
        _browser = browser;
        _logger = logger;
    }

    public async Task<OnlineCinemaStream?> ResolveAsync(string pageUrl, CancellationToken ct = default)
    {
        var http = await TryResolveHttpAsync(pageUrl, ct);
        if (http != null)
        {
            return http;
        }

        _logger.LogInformation("Онлайн-кинотеатр: HTTP-резолв не удался — фолбэк на скрытый браузер: {Url}", pageUrl);
        return await ResolveViaBrowserAsync(pageUrl, ct);
    }

    // ---------- pure-HTTP path ----------

    private async Task<OnlineCinemaStream?> TryResolveHttpAsync(string pageUrl, CancellationToken ct)
    {
        try
        {
            if (!KinogoSite.IsValidPageUrl(pageUrl))
            {
                return null;
            }

            _logger.LogInformation("Онлайн-кинотеатр: резолв по HTTP, движок curl={Curl}.",
                await CurlHttp.GetAvailabilityAsync());

            var filmHtml = await GetStringAsync(pageUrl, referer: KinogoSite.BaseUrl + "/", iframe: false, ct);
            if (filmHtml == null || KinogoSite.LooksLikeChallenge(filmHtml))
            {
                return null;
            }

            foreach (var embedUrl in KinogoSite.ExtractEmbedUrls(filmHtml).Take(3))
            {
                var stream = await TryResolveEmbedAsync(embedUrl, pageUrl, ct);
                if (stream != null)
                {
                    _logger.LogInformation(
                        "Онлайн-кинотеатр: поток получен по HTTP для {Url}: дорожек {Tracks} — {Hit}",
                        pageUrl, stream.Tracks.Count, stream.Url.Length > 100 ? stream.Url[..100] : stream.Url);
                    return stream;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Онлайн-кинотеатр: HTTP-резолв не удался для {Url}.", pageUrl);
        }

        return null;
    }

    private async Task<OnlineCinemaStream?> TryResolveEmbedAsync(string embedUrl, string pageUrl, CancellationToken ct)
    {
        var embedHtml = await GetStringAsync(embedUrl, referer: pageUrl, iframe: true, ct);
        if (embedHtml == null)
        {
            _logger.LogInformation("Онлайн-кинотеатр: embed недоступен по HTTP: {Url}", embedUrl);
            return null;
        }

        var fileMatch = System.Text.RegularExpressions.Regex.Match(
            embedHtml, @"""file"":""(#[^""]+)""");
        if (!fileMatch.Success)
        {
            _logger.LogInformation("Онлайн-кинотеатр: в embed нет #2-плейлиста: {Url}", embedUrl);
            return null;
        }

        var tracks = CinemarDecoder.DecodePlaylist(fileMatch.Groups[1].Value)
            .Where(t => !string.IsNullOrWhiteSpace(t.Data))
            .Take(12)
            .ToList();
        if (tracks.Count == 0)
        {
            return null;
        }

        var origin = new Uri(embedUrl).GetLeftPart(System.UriPartial.Authority);
        // Every track is one POST — voiceovers for films, episodes for series
        var resolved = await Task.WhenAll(tracks.Select(async t =>
        {
            var url = await PostPlaylistLoadAsync(origin, t.Data, embedUrl, ct);
            return url == null
                ? null
                : new OnlineCinemaTrack { Label = CleanTrackLabel(t.Title), Url = url };
        }));

        var stream = new OnlineCinemaStream();
        foreach (var track in resolved.Where(t => t != null).Cast<OnlineCinemaTrack>())
        {
            await FillVariantsAsync(track);
            stream.Tracks.Add(track);
        }

        if (stream.Tracks.Count == 0)
        {
            return null;
        }

        stream.Url = stream.Tracks[0].Url;
        stream.Label = stream.Tracks[0].Label;
        foreach (var kv in stream.Tracks[0].Variants)
        {
            stream.Variants[kv.Key] = kv.Value;
        }

        return stream;
    }

    // Track titles carry flag <img> tags and entities from the embed markup —
    // strip them down to the plain voiceover name
    private static string CleanTrackLabel(string title)
    {
        var clean = System.Text.RegularExpressions.Regex.Replace(title ?? string.Empty, @"<[^>]*>", " ");
        clean = System.Net.WebUtility.HtmlDecode(clean);
        return System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
    }

    private async Task<string?> PostPlaylistLoadAsync(string origin, string trackData, string embedUrl, CancellationToken ct)
    {
        try
        {
            var api = origin + "/api/playlist/load";
            string? body;
            var curlPosted = false;
            if (await CurlHttp.GetAvailabilityAsync())
            {
                var posted = await CurlHttp.PostJsonAsync(api, JsonSerializer.Serialize(trackData), embedUrl, origin, ct);
                curlPosted = posted is { Status: 200 };
                body = curlPosted ? posted!.Body : null;
                if (!curlPosted)
                {
                    _logger.LogInformation(
                        "Онлайн-кинотеатр: api/playlist/load через curl ответил {Status}.", posted?.Status ?? 0);
                }
            }
            else
            {
                using var msg = new HttpRequestMessage(HttpMethod.Post, api)
                {
                    Content = new StringContent(JsonSerializer.Serialize(trackData), Encoding.UTF8, "application/json")
                };
                msg.Headers.TryAddWithoutValidation("Referer", embedUrl);
                msg.Headers.TryAddWithoutValidation("Origin", origin);
                msg.Headers.TryAddWithoutValidation("User-Agent", KinogoSite.UserAgent);
                msg.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                using var resp = await Http.SendAsync(msg, ct);
                body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Онлайн-кинотеатр: api/playlist/load ответил {Status}.", (int)resp.StatusCode);
                    return null;
                }
            }

            using var doc = JsonDocument.Parse(body);
            var file = doc.RootElement.TryGetProperty("file", out var f) ? f.GetString() : null;
            return string.IsNullOrWhiteSpace(file) ? null : file;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Онлайн-кинотеатр: POST api/playlist/load не удался.");
            return null;
        }
    }

    private static async Task<string?> GetStringAsync(string url, string referer, bool iframe, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        // curl.exe first — its TLS passes the WAF where .NET gets challenged
        if (await CurlHttp.GetAvailabilityAsync())
        {
            var result = await CurlHttp.GetAsync(url, referer, iframe, ct);
            if (result is { Status: 200 })
            {
                return result.Body;
            }

            return null;
        }

        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        if (iframe)
        {
            // The embed sits in an iframe — a cross-site subresource fetch
            msg.Headers.TryAddWithoutValidation("User-Agent", KinogoSite.UserAgent);
            msg.Headers.TryAddWithoutValidation("Referer", referer);
            msg.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "iframe");
            msg.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
            msg.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        }
        else
        {
            KinogoSite.ApplyBrowserHeaders(msg);
        }

        using var resp = await Http.SendAsync(msg, ct);
        var html = await resp.Content.ReadAsStringAsync(ct);
        return resp.IsSuccessStatusCode ? html : null;
    }

    // Fetches the master playlist and parses its renditions so the quality
    // picker has options; failure keeps the plain link (media playlist case)
    private static async Task FillVariantsAsync(OnlineCinemaTrack track)
    {
        try
        {
            var master = await Http.GetStringAsync(track.Url);
            var variants = StreamService.ParseHlsMasterVariants(master, new Uri(track.Url));
            foreach (var kv in variants)
            {
                track.Variants[kv.Key] = kv.Value;
            }

            track.Variants["Авто"] = track.Url;
        }
        catch (Exception)
        {
            // Master fetch is best-effort — playback falls back to the plain link
        }
    }

    // ---------- WebView2 fallback ----------

    private async Task<OnlineCinemaStream?> ResolveViaBrowserAsync(string pageUrl, CancellationToken ct)
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
            // Parser flow: wait for the embed iframe, then CDP-click it — the
            // site's own handler loads the player and fires the stream requests
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
                    if (hit != null && ExtractStream(hit) is { } resolved)
                    {
                        var track = new OnlineCinemaTrack { Label = resolved.Label, Url = resolved.Url };
                        await FillVariantsAsync(track);
                        resolved.Tracks.Add(track);
                        resolved.Url = track.Url;
                        foreach (var kv in track.Variants)
                        {
                            resolved.Variants[kv.Key] = kv.Value;
                        }

                        _logger.LogInformation(
                            "Онлайн-кинотеатр: поток получен для {Url} (кликов: {Clicks}), вариантов {Variants}: {Hit}",
                            pageUrl, clicked, resolved.Variants.Count, hit.Url.Length > 100 ? hit.Url[..100] : hit.Url);
                        return resolved;
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
