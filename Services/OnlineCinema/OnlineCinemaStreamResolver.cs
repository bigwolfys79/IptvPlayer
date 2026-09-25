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

    // Resolves one voiceover leaf (season/episode selection) — POST
    // api/playlist/load with the leaf payload + master rendition parse
    Task<OnlineCinemaStream?> ResolveLeafAsync(string origin, string data, string embedUrl,
        CancellationToken ct = default);
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
                        "Онлайн-кинотеатр: поток получен по HTTP для {Url} ({Kind}) — {Hit}",
                        pageUrl, stream.Playlist?.Seasons != null ? "сериал" : "фильм",
                        stream.Url.Length > 100 ? stream.Url[..100] : stream.Url);
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

    // Resolves a single voiceover leaf (season/episode picker) — POST
    // api/playlist/load with its payload, then parse master renditions
    public async Task<OnlineCinemaStream?> ResolveLeafAsync(string origin, string data, string embedUrl,
        CancellationToken ct = default)
    {
        var url = await PostPlaylistLoadAsync(origin, data, embedUrl, ct);
        if (url == null)
        {
            return null;
        }

        var stream = new OnlineCinemaStream { Url = url };
        CopyVariants(await GetVariantsAsync(url), stream.Variants);
        return stream;
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

        var nodes = CinemarDecoder.DecodePlaylist(fileMatch.Groups[1].Value);
        var isSeries = nodes.Any(n => n.Folder is { Count: > 0 });
        var origin = new Uri(embedUrl).GetLeftPart(System.UriPartial.Authority);

        if (!isSeries)
        {
            // Film: every voiceover is pre-resolved so switching is instant
            var leaves = CinemarDecoder.FlattenLeaves(nodes).Take(12).ToList();
            if (leaves.Count == 0)
            {
                return null;
            }

            var gate = new SemaphoreSlim(3, 3);
            var voiceovers = (await Task.WhenAll(leaves.Select(async leaf =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var url = await PostPlaylistLoadAsync(origin, leaf.Data, embedUrl, ct);
                    return url == null
                        ? null
                        : new OnlineCinemaLeaf
                        {
                            Label = leaf.Label,
                            Data = leaf.Data,
                            Origin = origin,
                            EmbedUrl = embedUrl,
                            ResolvedUrl = url
                        };
                }
                finally
                {
                    gate.Release();
                }
            }))).Where(v => v != null).Cast<OnlineCinemaLeaf>().ToList();

            if (voiceovers.Count == 0)
            {
                return null;
            }

            var filmStream = new OnlineCinemaStream
            {
                Label = voiceovers[0].Label,
                Url = voiceovers[0].ResolvedUrl!,
                Playlist = new OnlineCinemaPlaylist { Voiceovers = voiceovers }
            };
            CopyVariants(await GetVariantsAsync(filmStream.Url), filmStream.Variants);
            return filmStream;
        }

        // Series: seasons → episodes → voiceovers; leaves resolve lazily at
        // selection time (a series can carry dozens of them)
        var playlist = new OnlineCinemaPlaylist { Seasons = new List<OnlineCinemaSeason>() };
        foreach (var seasonNode in nodes)
        {
            var season = new OnlineCinemaSeason { Label = seasonNode.Title };
            foreach (var episodeNode in seasonNode.Folder ?? [])
            {
                var episode = new OnlineCinemaEpisode
                {
                    Label = string.IsNullOrWhiteSpace(episodeNode.Title2)
                        ? episodeNode.Title
                        : episodeNode.Title2
                };
                foreach (var voiceNode in episodeNode.Folder ?? [])
                {
                    if (string.IsNullOrWhiteSpace(voiceNode.Data))
                    {
                        continue;
                    }

                    episode.Voiceovers.Add(new OnlineCinemaLeaf
                    {
                        Label = CleanTrackLabel(voiceNode.Title),
                        Data = voiceNode.Data,
                        Origin = origin,
                        EmbedUrl = embedUrl
                    });
                }

                if (episode.Voiceovers.Count > 0)
                {
                    season.Episodes.Add(episode);
                }
            }

            if (season.Episodes.Count > 0)
            {
                playlist.Seasons.Add(season);
            }
        }

        if (playlist.Seasons.Count == 0)
        {
            return null;
        }

        // Default start: season 1, episode 1, first voiceover
        var defaultLeaf = playlist.Seasons[0].Episodes[0].Voiceovers[0];
        var defaultStream = await ResolveLeafAsync(defaultLeaf.Origin, defaultLeaf.Data, defaultLeaf.EmbedUrl, ct);
        if (defaultStream == null)
        {
            return null;
        }

        defaultStream.Playlist = playlist;
        return defaultStream;
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

            if (body == null)
            {
                return null;
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
    private static async Task<Dictionary<string, string>> GetVariantsAsync(string url)
    {
        var variants = new Dictionary<string, string>();
        try
        {
            var master = await Http.GetStringAsync(url);
            variants = StreamService.ParseHlsMasterVariants(master, new Uri(url));
            variants["Авто"] = url;
        }
        catch (Exception)
        {
            // Master fetch is best-effort — playback falls back to the plain link
        }

        return variants;
    }

    private static void CopyVariants(Dictionary<string, string> variants, Dictionary<string, string> target)
    {
        foreach (var kv in variants)
        {
            target[kv.Key] = kv.Value;
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
                        CopyVariants(await GetVariantsAsync(resolved.Url), resolved.Variants);
                        resolved.Playlist = new OnlineCinemaPlaylist
                        {
                            Voiceovers =
                            [
                                new OnlineCinemaLeaf
                                {
                                    Label = resolved.Label,
                                    Origin = resolved.Url,
                                    ResolvedUrl = resolved.Url
                                }
                            ]
                        };
                        resolved.Label = "поток";
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
