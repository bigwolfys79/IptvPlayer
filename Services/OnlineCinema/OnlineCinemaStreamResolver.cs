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

    // Direct-MP4 download renditions of one voiceover leaf: POST
    // api/playlist/load for the download token, then api/player/download
    Task<List<OnlineCinemaDownloadOption>> GetDownloadOptionsAsync(string origin, string data, string embedUrl,
        CancellationToken ct = default);

    // Fresh HLS URL (mini-master for the quality, or the master) for an
    // nextembed-style film — used to restart/refresh HLS downloads whose
    // link token expired. episodeKey ("s<season>e<episode>") picks the exact
    // series episode
    Task<string?> ResolveFreshHlsUrlAsync(string pageUrl, string? quality, string? episodeKey = null,
        CancellationToken ct = default);

    // Fills a nextembed series leaf's Variants (mini-masters per quality) so
    // playback keeps the single audio track and the quality picker works
    Task EnrichNextEmbedLeafVariantsAsync(OnlineCinemaLeaf leaf, CancellationToken ct = default);
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
    private readonly LocalStreamProxy _proxy;
    private readonly ILogger _logger;

    public OnlineCinemaStreamResolver(OnlineCinemaBrowserService browser, LocalStreamProxy proxy,
        ILogger<OnlineCinemaStreamResolver> logger)
    {
        _browser = browser;
        _proxy = proxy;
        _logger = logger;
    }

    public async Task<OnlineCinemaStream?> ResolveAsync(string pageUrl, CancellationToken ct = default)
    {
        var http = await TryResolveHttpAsync(pageUrl, ct);
        if (http != null)
        {
            return http;
        }

        if (!OnlineCinemaMethods.UseWebView2)
        {
            return null; // hidden browser disabled in settings
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
        var (url, _) = await PostPlaylistLoadAsync(origin, data, embedUrl, ct);
        if (url == null)
        {
            return null;
        }

        var stream = new OnlineCinemaStream { Url = url };
        CopyVariants(await GetVariantsAsync(url), stream.Variants);
        return stream;
    }

    // Direct-MP4 download renditions of one voiceover leaf: the playlist/load
    // response carries a "download" token, api/player/download turns it into
    // the download-window HTML with one link per quality
    public async Task<List<OnlineCinemaDownloadOption>> GetDownloadOptionsAsync(string origin, string data,
        string embedUrl, CancellationToken ct = default)
    {
        var (_, token) = await PostPlaylistLoadAsync(origin, data, embedUrl, ct);
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogInformation("Онлайн-кинотеатр: в ответе playlist/load нет download-токена.");
            return [];
        }

        var html = await PostApiAsync(origin, "/api/player/download", JsonSerializer.Serialize(token), embedUrl, ct);
        if (html == null)
        {
            return [];
        }

        return ParseDownloadWindowHtml(ExtractDownloadWindowHtml(html));
    }

    // api/player/download answers either a JSON array ["<html>", ...] or the
    // raw HTML — reduce both to the window markup
    private static string ExtractDownloadWindowHtml(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Array &&
                    doc.RootElement.GetArrayLength() > 0 &&
                    doc.RootElement[0].ValueKind == JsonValueKind.String)
                {
                    return doc.RootElement[0].GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                // Not a JSON array — treat the body as raw HTML below
            }
        }

        return body;
    }

    // The window markup labels are shifted (anchor text may not match the
    // file) — the quality is taken from the URL suffix, /1080.mp4 -> "1080p"
    public static List<OnlineCinemaDownloadOption> ParseDownloadWindowHtml(string html)
    {
        var options = new List<OnlineCinemaDownloadOption>();
        if (string.IsNullOrEmpty(html))
        {
            return options;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(
                     html, @"<a\s+href=""(?<url>[^""]+\.mp4[^""]*)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var url = match.Groups["url"].Value;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var height = System.Text.RegularExpressions.Regex.Match(url, @"/(?<h>\d{3,4})\.mp4$");
            if (!height.Success || !seen.Add(height.Groups["h"].Value))
            {
                continue;
            }

            options.Add(new OnlineCinemaDownloadOption
            {
                Quality = height.Groups["h"].Value + "p",
                Url = url
            });
        }

        return options.OrderByDescending(o => int.Parse(o.Quality[..^1])).ToList();
    }

    // Rebuild one mini-master per quality: header + the primary EXT-X-MEDIA
    // audio group line + that single variant, served through the local proxy.
    // The child playlists stay on the CDN (absolute URLs); switching quality
    // plays the mini-master, so audio survives the switch. Failover duplicate
    // groups and renditions are dropped — they only produce identical audio
    // tracks in the picker
    private void BuildAudioGroupMasters(string master, Uri masterUri,
        Dictionary<string, string> variants)
    {
        try
        {
            string? audioGroupLine = null;
            var audioGroupId = string.Empty;
            var renditionLines = new List<(string Inf, string Url)>();
            string? pendingInf = null;
            foreach (var rawLine in master.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("#EXT-X-MEDIA", StringComparison.Ordinal))
                {
                    if (audioGroupLine == null)
                    {
                        audioGroupLine = line;
                        var group = System.Text.RegularExpressions.Regex.Match(
                            line, @"GROUP-ID=""(?<g>[^""]+)""");
                        audioGroupId = group.Success ? group.Groups["g"].Value : string.Empty;
                    }
                }
                else if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
                {
                    pendingInf = line;
                }
                else if (!line.StartsWith('#') && pendingInf != null)
                {
                    renditionLines.Add((pendingInf, new Uri(masterUri, line).ToString()));
                    pendingInf = null;
                }
            }

            if (audioGroupLine == null)
            {
                return;
            }

            // One variant per resolution, first wins — mirrors ParseHlsMasterVariants;
            // variants bound to other (failover) audio groups are skipped
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var written = 0;
            foreach (var (inf, url) in renditionLines)
            {
                var resolution = System.Text.RegularExpressions.Regex.Match(
                    inf, @"RESOLUTION=(?<r>\d+x\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!resolution.Success || !seen.Add(resolution.Groups["r"].Value))
                {
                    continue;
                }

                var audioAttr = System.Text.RegularExpressions.Regex.Match(
                    inf, @"AUDIO=""(?<g>[^""]+)""");
                if (audioAttr.Success && audioAttr.Groups["g"].Value != audioGroupId)
                {
                    continue;
                }

                var quality = resolution.Groups["r"].Value.Split('x')[1] + "p";
                var miniMaster = "#EXTM3U\n" + audioGroupLine + "\n" + inf + "\n" + url + "\n";
                var local = _proxy.RegisterText(miniMaster, ".m3u8");
                if (local.Length > 0)
                {
                    variants[quality] = local;
                    written++;
                }
            }
        }
        catch (Exception)
        {
            // Mini-masters are an enhancement — playback falls back to the
            // plain master link
        }
    }

    // Re-resolves the film page and returns a fresh HLS URL for the stored
    // quality (mini-master via proxy, or the master itself)
    public async Task<string?> ResolveFreshHlsUrlAsync(string pageUrl, string? quality, string? episodeKey = null,
        CancellationToken ct = default)
    {
        try
        {
            var filmHtml = await GetStringAsync(pageUrl, KinogoSite.BaseUrl + "/", false, ct);
            if (filmHtml == null || KinogoSite.LooksLikeChallenge(filmHtml))
            {
                return null;
            }

            foreach (var embedUrl in KinogoSite.ExtractEmbedUrls(filmHtml).Take(3))
            {
                var embedHtml = await GetStringAsync(embedUrl, pageUrl, true, ct);
                if (embedHtml == null)
                {
                    continue;
                }

                var hls = System.Text.RegularExpressions.Regex.Match(
                    embedHtml, @"hls:\s*""(?<url>[^""]+\.m3u8[^""]*)""");
                if (!hls.Success)
                {
                    continue;
                }

                var masterUrl = hls.Groups["url"].Value.Replace("\\/", "/");

                // Series: resolve the stored episode inside the fresh config
                var seasonsJson = ExtractJsonArray(embedHtml, "seasons:[");
                if (seasonsJson != null)
                {
                    var probe = new OnlineCinemaStream();
                    if (BuildNextEmbedSeries(seasonsJson, probe) && probe.Playlist?.Seasons is { } ss)
                    {
                        foreach (var season in ss)
                        {
                            foreach (var episode in season.Episodes)
                            {
                                foreach (var leaf in episode.Voiceovers)
                                {
                                    if (episodeKey == null ||
                                        $"s{leaf.SeasonNumber}e{leaf.EpisodeNumber}" == episodeKey)
                                    {
                                        return leaf.ResolvedUrl;
                                    }
                                }
                            }
                        }
                    }

                    continue;
                }

                try
                {
                    var master = await Http.GetStringAsync(masterUrl);
                    var variants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (master.Contains("#EXT-X-MEDIA"))
                    {
                        BuildAudioGroupMasters(master, new Uri(masterUrl), variants);
                    }
                    else
                    {
                        variants = StreamService.ParseHlsMasterVariants(master, new Uri(masterUrl));
                    }

                    if (!string.IsNullOrEmpty(quality) && variants.TryGetValue(quality, out var fresh))
                    {
                        return fresh;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Онлайн-кинотеатр: не удалось разобрать мастер для докачки.");
                }

                return masterUrl;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Онлайн-кинотеатр: не удалось обновить HLS-ссылку для {Url}.", pageUrl);
        }

        return null;
    }

    // Extracts the balanced JSON array that follows the marker (inclusive)
    private static string? ExtractJsonArray(string html, string marker)
    {
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length - 1; // at the opening '['
        var depth = 0;
        var inString = false;
        for (var i = start; i < html.Length; i++)
        {
            var c = html[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
                if (depth == 0)
                {
                    return html.Substring(start, i - start + 1);
                }
            }
        }

        return null;
    }

    // Builds the seasons/episodes tree from the nextembed config's
    // "seasons":[...] array. Each episode is a pre-resolved leaf carrying its
    // own master .m3u8 from the provider
    private static bool BuildNextEmbedSeries(string seasonsJson, OnlineCinemaStream stream)
    {
        try
        {
            using var doc = JsonDocument.Parse(seasonsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var seasons = new List<(int Num, OnlineCinemaSeason Season)>();
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                var seasonNum = s.TryGetProperty("season", out var sv) && sv.ValueKind == JsonValueKind.Number
                    ? sv.GetInt32()
                    : 0;
                var season = new OnlineCinemaSeason { Label = $"Сезон {seasonNum}" };
                if (s.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array)
                {
                    var parsed = new List<(int Num, OnlineCinemaEpisode Episode)>();
                    var index = 0;
                    foreach (var ep in eps.EnumerateArray())
                    {
                        index++;
                        var hls = ep.TryGetProperty("hls", out var h) ? h.GetString() : null;
                        if (string.IsNullOrEmpty(hls))
                        {
                            continue;
                        }

                        var num = ep.TryGetProperty("episode", out var ev) &&
                                  int.TryParse(ev.GetString(), out var n)
                            ? n
                            : index;
                        var audio = string.Empty;
                        if (ep.TryGetProperty("audio", out var au) &&
                            au.TryGetProperty("names", out var names) &&
                            names.ValueKind == JsonValueKind.Array)
                        {
                            audio = string.Join(", ", names.EnumerateArray()
                                .Select(x => x.GetString() ?? string.Empty)
                                .Where(x => x.Length > 0));
                        }

                        var episode = new OnlineCinemaEpisode { Label = $"Серия {num}" };
                        episode.Voiceovers.Add(new OnlineCinemaLeaf
                        {
                            Label = audio.Length > 0 ? audio : "Оригинал",
                            ResolvedUrl = hls,
                            SeasonNumber = seasonNum,
                            EpisodeNumber = num
                        });
                        parsed.Add((num, episode));
                    }

                    foreach (var (_, episode) in parsed.OrderBy(e => e.Num))
                    {
                        season.Episodes.Add(episode);
                    }
                }

                if (season.Episodes.Count > 0)
                {
                    seasons.Add((seasonNum, season));
                }
            }

            if (seasons.Count == 0)
            {
                return false;
            }

            var ordered = seasons.OrderBy(s => s.Num).ToList();
            stream.Playlist = new OnlineCinemaPlaylist
            {
                Seasons = ordered.Select(s => s.Season).ToList()
            };
            // Default start: the lowest season, its first episode
            stream.Url = ordered[0].Season.Episodes[0].Voiceovers[0].ResolvedUrl!;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Fetches the episode's master playlist and caches per-quality
    // mini-masters (single audio group) on the leaf. Returns the playback URL
    // for the best rendition — mirroring the film path
    public async Task EnrichNextEmbedLeafVariantsAsync(OnlineCinemaLeaf leaf, CancellationToken ct = default)
    {
        if (leaf.ResolvedUrl == null || leaf.Variants.Count > 0 || !string.IsNullOrEmpty(leaf.Data))
        {
            return;
        }

        try
        {
            var master = await Http.GetStringAsync(leaf.ResolvedUrl);
            var variants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (master.Contains("#EXT-X-MEDIA"))
            {
                BuildAudioGroupMasters(master, new Uri(leaf.ResolvedUrl), variants);
            }
            else
            {
                variants = StreamService.ParseHlsMasterVariants(master, new Uri(leaf.ResolvedUrl));
            }

            foreach (var kv in variants)
            {
                leaf.Variants[kv.Key] = kv.Value;
            }

            leaf.Variants["Авто"] = leaf.ResolvedUrl;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Онлайн-кинотеатр: не удалось обогатить варианты эпизода.");
        }
    }

    private async Task<OnlineCinemaStream?> TryResolveEmbedAsync(string embedUrl, string pageUrl, CancellationToken ct)
    {
        var embedHtml = await GetStringAsync(embedUrl, referer: pageUrl, iframe: true, ct);
        if (embedHtml == null)
        {
            _logger.LogInformation("Онлайн-кинотеатр: embed недоступен по HTTP: {Url}", embedUrl);
            return null;
        }

        // Series first: the config carries the full seasons/episodes tree
        // ("seasons":[...]) with a per-episode HLS link — no top-level `hls:`
        var seasonsJson = ExtractJsonArray(embedHtml, "seasons:[");
        if (seasonsJson != null)
        {
            var seriesStream = new OnlineCinemaStream { Label = "поток", IsPlainHls = true };
            if (BuildNextEmbedSeries(seasonsJson, seriesStream))
            {
                _logger.LogInformation(
                    "Онлайн-кинотеатр: поток получен из hls-конфига {Url} (сезонов: {Seasons}) — {Hit}",
                    embedUrl, seriesStream.Playlist?.Seasons?.Count ?? 0,
                    seriesStream.Url.Length > 100 ? seriesStream.Url[..100] : seriesStream.Url);
                return seriesStream;
            }
        }

        // Second provider family (nextembed-style): the player config carries
        // a plain master .m3u8 under `hls:` — no "#2" playlist, no voiceover
        // tree, quality variants come from the master itself
        var hlsMatch = System.Text.RegularExpressions.Regex.Match(
            embedHtml, @"hls:\s*""(?<url>[^""]+\.m3u8[^""]*)""");
        if (hlsMatch.Success)
        {
            var stream = new OnlineCinemaStream
            {
                Url = hlsMatch.Groups["url"].Value.Replace("\\/", "/"),
                Label = "поток"
            };

            var master = await Http.GetStringAsync(stream.Url);
            if (!master.Contains("#EXT-X-MEDIA"))
            {
                // Muxed variants — quality switching keeps working
                CopyVariants(await GetVariantsAsync(stream.Url), stream.Variants);
            }
            else
            {
                // Variants are video-only; audio rides in an EXT-X-MEDIA
                // group that only the master carries. Playing a variant
                // directly loses audio, so build one local mini-master per
                // quality (audio group + that single variant) — switching
                // quality plays the local file, ffmpeg pulls CDN playlists
                BuildAudioGroupMasters(master, new Uri(stream.Url), stream.Variants);
            }
            _logger.LogInformation(
                "Онлайн-кинотеатр: поток получен из hls-конфига {Url} (аудио-группа: {Audio}, вариантов: {Variants}) — {Hit}",
                embedUrl, master.Contains("#EXT-X-MEDIA") ? "да" : "нет", stream.Variants.Count,
                stream.Url.Length > 100 ? stream.Url[..100] : stream.Url);
            return stream;
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
                    var (url, _) = await PostPlaylistLoadAsync(origin, leaf.Data, embedUrl, ct);
                    if (url == null)
                    {
                        return null;
                    }

                    var resolved = new OnlineCinemaLeaf
                    {
                        Label = CleanTrackLabel(leaf.Label),
                        Data = leaf.Data,
                        Origin = origin,
                        EmbedUrl = embedUrl,
                        ResolvedUrl = url
                    };
                    CopyVariants(await GetVariantsAsync(url), resolved.Variants);
                    return resolved;
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
            CopyVariants(voiceovers[0].Variants, filmStream.Variants);
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

        CopyVariants(defaultStream.Variants, defaultLeaf.Variants);
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

    private async Task<(string? File, string? DownloadToken)> PostPlaylistLoadAsync(
        string origin, string trackData, string embedUrl, CancellationToken ct)
    {
        try
        {
            var body = await PostApiAsync(origin, "/api/playlist/load", JsonSerializer.Serialize(trackData), embedUrl, ct);
            if (body == null)
            {
                return (null, null);
            }

            using var doc = JsonDocument.Parse(body);
            var file = doc.RootElement.TryGetProperty("file", out var f) ? f.GetString() : null;
            var download = doc.RootElement.TryGetProperty("download", out var d) ? d.GetString() : null;
            return (string.IsNullOrWhiteSpace(file) ? null : file,
                    string.IsNullOrWhiteSpace(download) ? null : download);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Онлайн-кинотеатр: POST api/playlist/load не удался.");
            return (null, null);
        }
    }

    // POST JSON to the player API: curl.exe first (its TLS passes the WAF),
    // HttpClient fallback; both methods disabled in settings -> null
    private async Task<string?> PostApiAsync(string origin, string apiPath, string jsonBody, string embedUrl,
        CancellationToken ct)
    {
        try
        {
            var api = origin + apiPath;
            if (OnlineCinemaMethods.UseCurl && await CurlHttp.GetAvailabilityAsync())
            {
                var posted = await CurlHttp.PostJsonAsync(api, jsonBody, embedUrl, origin, ct);
                if (posted is { Status: 200 })
                {
                    return posted!.Body;
                }

                _logger.LogInformation("Онлайн-кинотеатр: {Path} через curl ответил {Status}.",
                    apiPath, posted?.Status ?? 0);
                return null;
            }

            if (!OnlineCinemaMethods.UseHttpClient)
            {
                return null; // both HTTP methods disabled in settings
            }

            using var msg = new HttpRequestMessage(HttpMethod.Post, api)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            msg.Headers.TryAddWithoutValidation("Referer", embedUrl);
            msg.Headers.TryAddWithoutValidation("Origin", origin);
            msg.Headers.TryAddWithoutValidation("User-Agent", KinogoSite.UserAgent);
            msg.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            using var resp = await Http.SendAsync(msg, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("Онлайн-кинотеатр: {Path} ответил {Status}.", apiPath, (int)resp.StatusCode);
                return null;
            }

            return body;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Онлайн-кинотеатр: POST {Path} не удался.", apiPath);
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
        if (OnlineCinemaMethods.UseCurl && await CurlHttp.GetAvailabilityAsync())
        {
            var result = await CurlHttp.GetAsync(url, referer, iframe, ct);
            if (result is { Status: 200 })
            {
                return result.Body;
            }

            return null;
        }

        if (!OnlineCinemaMethods.UseHttpClient)
        {
            return null; // both HTTP methods disabled in settings
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

        try
        {
            _browser.ClearPlaylistResponses();
            if (!await _browser.NavigateAsync(pageUrl, ct))
            {
                _logger.LogWarning(
                    "Онлайн-кинотеатр: страница фильма не открылась (или сайт требует проверку): {Url}", pageUrl);
                return null;
            }

            return await ResolveCoreAsync(pageUrl, ct);
        }
        finally
        {
            // The hidden browser is not needed once the stream link is known —
            // otherwise the site player keeps running in the background
            await _browser.ShutdownAsync();
        }
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
                    // P2P players (sevstar) fetch a .txt playlist the system
                    // player cannot open — wait for a direct .m3u8 hit instead
                    if (hit != null && hit.Url.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation(
                            "Онлайн-кинотеатр: p2p-плейлист пропущен, жду прямую m3u8 — {Url}",
                            hit.Url.Length > 100 ? hit.Url[..100] : hit.Url);
                        continue;
                    }

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
