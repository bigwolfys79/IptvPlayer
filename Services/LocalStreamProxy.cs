using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;


public sealed class LocalStreamProxy : IDisposable
{
    private const int WindowSamples = 5;
    // A silent upstream body longer than this is a dead node: fail fast so
    // the player's re-request reaches failover instead of hanging 120 s
    private static readonly TimeSpan BodyIdleTimeout = TimeSpan.FromSeconds(15);
    // Trickling-node guard: a segment body delivering less than this per
    // window is stalling regardless of never going fully silent
    private const long MinSegmentWindowBytes = 64 * 1024;
    private static readonly TimeSpan ThroughputWindow = TimeSpan.FromSeconds(5);
    // Slow-segment telemetry: segments arriving near real-time leave the
    // demuxer without headroom — visible as jitter, invisible to idle checks
    private static readonly TimeSpan SlowSegmentLogEvery = TimeSpan.FromSeconds(5);
    private long _lastSlowSegmentLog;
    private static readonly TimeSpan RedirectLogEvery = TimeSpan.FromSeconds(10);
    private long _lastRedirectLog;
    // Parallel ranged prefetch: a node throttling one connection to ~real-time
    // speed is multiplied by the chunk count, so the cache always beats the demuxer
    private const int ParallelChunkCount = 4;
    private const long ParallelChunkMinBytes = 2 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly Microsoft.Extensions.Logging.ILogger<LocalStreamProxy> _logger;
    private readonly object _gate = new();
    private readonly object _virtualGate = new();
    private readonly Dictionary<string, string> _virtualTexts = new(StringComparer.Ordinal);
    private readonly Queue<double> _window = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private long _totalBytes;
    private long _lastSampleBytes;
    private readonly System.Diagnostics.Stopwatch _sampleClock = System.Diagnostics.Stopwatch.StartNew();
    private string _baseUrl = "";
    private long _tenSecondBase;
    private readonly System.Diagnostics.Stopwatch _tenSecondClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Queue<double> _tenSecondRates = new();
    private double? _minTenSecondRate;
    // CDN nodes learned from playlists (host -> auth token query): a failed
    // segment fetch is retried transparently on an alternate node
    private readonly Dictionary<string, string> _nodeTokens = new(StringComparer.OrdinalIgnoreCase);
    // Slow-node bookkeeping: a stalled delivery demotes a node immediately so
    // new segment requests go to a healthy alternate; repeat offenders earn
    // an exponentially longer cooldown
    private const int NodeDemoteMaxMinutes = 15;
    private static readonly TimeSpan NodeDemoteFor = TimeSpan.FromMinutes(3);
    private readonly Dictionary<string, (int DemoteCount, DateTime DemotedUntil)> _nodeHealth = new(StringComparer.OrdinalIgnoreCase);
    // Prefetched segments: announced by the last playlist, pulled in the
    // background so the demuxer's request is served from the local cache
    // instead of waiting on a slow CDN node
    private const int PrefetchSegmentCount = 8;
    private const int MaxCachedSegments = 12;
    private const long MaxCacheBytes = 48 * 1024 * 1024;
    private readonly Dictionary<string, (byte[] Data, string ContentType)> _segmentCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _prefetchInFlight = new(StringComparer.Ordinal);
    private long _cacheBytes;
    private int _generation;
    private bool _prefetchHitLogged;
    private bool _prefetchStartLogged;
    private bool _prefetchFailLogged;
    private bool _nodeCountLogged;
    private bool _cacheMissLogged;
    private bool _cacheInflightLogged;
    private bool _segRequestLogged;
    // Verbose proxy telemetry respects the diagnostics dialog toggle
    private static bool DiagnosticsEnabled => App.TempDiagnosticsEnabled;

    public LocalStreamProxy(Microsoft.Extensions.Logging.ILogger<LocalStreamProxy> logger)
    {
        _logger = logger;
        _http = new HttpClient(new SocketsHttpHandler
        {

            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
        })
        {
            // Generous ceiling: a slow CDN node may legitimately need a long
            // time to deliver one segment; the header phase has its own cap
            Timeout = TimeSpan.FromSeconds(120),
        };
    }

    public bool IsRunning => _listener is not null;


    // Registers a static playlist (online-cinema mini-masters) served at a
    // stable local URL; empty string when the listener cannot start
    public string RegisterText(string content, string extension)
    {
        if (!IsRunning && !TryStart())
        {
            return string.Empty;
        }

        var name = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16]
            + extension;
        lock (_virtualGate)
        {
            _virtualTexts[name] = content;
        }

        return _baseUrl + "/v/" + name;
    }


    public string WrapUrl(string upstreamUrl)
    {
        if (!IsRunning && !TryStart())
        {
            // Auto-fallback: a dead proxy must not kill playback
            _logger.LogWarning("Прокси не запустился — поток идёт напрямую: {Url}", upstreamUrl);
            return upstreamUrl;
        }

        return _baseUrl + "/p/" + Encode(upstreamUrl) + ExtensionOf(upstreamUrl);
    }

    private static string ExtensionOf(string url)
    {
        try
        {
            var ext = new Uri(url).AbsolutePath;
            var dot = ext.LastIndexOf('.');
            if (dot < 0 || ext.Length - dot > 6)
            {
                return ".ts";
            }

            return ext[dot..];
        }
        catch (UriFormatException)
        {
            return ".ts";
        }
    }


    public void ResetForNewStream()
    {
        lock (_gate)
        {
            _totalBytes = 0;
            _lastSampleBytes = 0;
            _window.Clear();
            _tenSecondBase = _totalBytes;
            _tenSecondRates.Clear();
            _minTenSecondRate = null;
            _nodeTokens.Clear();
            _nodeHealth.Clear();
            _nodeCountLogged = false;
            // Stale segments are useless after a channel change
            _segmentCache.Clear();
            _prefetchInFlight.Clear();
            _cacheBytes = 0;
            _generation++;
            _prefetchHitLogged = false;
            _prefetchStartLogged = false;
            _prefetchFailLogged = false;
            _cacheMissLogged = false;
            _cacheInflightLogged = false;
            _segRequestLogged = false;
        }
    }


    // Minimum 10-second-window bitrate over the last minute: minute averages
    // hide sub-second stalls, while a bursty feed zeros some windows
    public double? MinBitrateLastMinute
    {
        get
        {
            lock (_gate)
            {
                return _minTenSecondRate;
            }
        }
    }

    private async Task SamplerLoopAsync(CancellationToken ct)
    {
        using var timer = new System.Threading.PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                double rate;
                lock (_gate)
                {
                    var seconds = _tenSecondClock.Elapsed.TotalSeconds;
                    _tenSecondClock.Restart();
                    var delta = _totalBytes - _tenSecondBase;
                    _tenSecondBase = _totalBytes;
                    if (seconds <= 0.5)
                    {
                        continue;
                    }

                    rate = delta * 8 / seconds;
                    _tenSecondRates.Enqueue(rate);
                    while (_tenSecondRates.Count > 6)
                    {
                        _tenSecondRates.Dequeue();
                    }

                    _minTenSecondRate = _tenSecondRates.Min();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }


    public double? Sample()
    {
        lock (_gate)
        {
            if (!IsRunning)
            {
                return null;
            }

            var seconds = _sampleClock.Elapsed.TotalSeconds;
            _sampleClock.Restart();
            var delta = _totalBytes - _lastSampleBytes;
            _lastSampleBytes = _totalBytes;

            if (_window.Count == 0 && delta == 0)
            {
                return null;
            }

            if (seconds > 0.2 && delta > 0)
            {
                _window.Enqueue(delta * 8 / seconds);
                while (_window.Count > WindowSamples)
                {
                    _window.Dequeue();
                }
            }

            if (_window.Count == 0)
            {
                return null;
            }

            var sorted = _window.OrderBy(x => x).ToArray();
            return sorted[sorted.Length / 2];
        }
    }

    private bool TryStart()
    {
        try
        {
            // Port 0 + single Start: no probe-then-bind window for another process to steal the port
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start(8);
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _baseUrl = $"http://127.0.0.1:{port}";

            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _ = Task.Run(() => SamplerLoopAsync(_cts.Token));
            return true;
        }
        catch (Exception)
        {
            _listener = null;
            _cts = null;
            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                continue;
            }

            if (ct.IsCancellationRequested)
            {
                client.Dispose();
                break;
            }

            _ = Task.Run(() => HandleConnectionAsync(client, ct), ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;

            try
            {
                client.ReceiveTimeout = 15_000;
                client.SendTimeout = 15_000;

                var stream = client.GetStream();
                var (path, headers) = await ReadRequestHeadAsync(stream, ct);
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                // Virtual static playlists (mini-masters) — no upstream fetch
                if (path.StartsWith("/v/", StringComparison.Ordinal))
                {
                    string? text;
                    lock (_virtualGate)
                    {
                        _virtualTexts.TryGetValue(path[3..], out text);
                    }

                    if (text is null)
                    {
                        await WriteSimpleStatusAsync(stream, 404, "Not Found", ct);
                        return;
                    }

                    await WriteTextPlaylistAsync(stream, text, ct);
                    return;
                }

                var upstream = DecodePath(path);
                if (upstream is null)
                {
                    await WriteSimpleStatusAsync(stream, 404, "Not Found", ct);
                    return;
                }

                var upstreamBase = new Uri(upstream);

                // Playlist URLs come from the master host — no failover there;
                // a failing playlist means dead tokens everywhere, the demuxer
                // will re-fetch the master itself
                var isPlaylistPath = upstreamBase.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                    || upstreamBase.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);

                // Prefetched segment — serve from the local cache at once
                if (!isPlaylistPath)
                {
                    var fullRange = IsFullRangeRequest(headers);
                    if (!_segRequestLogged)
                    {
                        _segRequestLogged = true;
                        _logger.LogInformation(
                            "Первый сегмент-запрос: Range={Range}, кэш-путь={UseCache}.",
                            headers.TryGetValue("Range", out var range) ? range : "нет",
                            fullRange ? "активен" : "пропущен");
                    }

                    if (fullRange && await TryServeFromCacheAsync(stream, upstream, ct))
                    {
                        return;
                    }
                }

                // A demoted slow node is skipped for segments; playlists stay
                // on the master host — dead tokens there mean dead everywhere
                var effective = isPlaylistPath ? upstreamBase : EffectiveSegmentUrl(upstreamBase);
                HttpResponseMessage response;
                try
                {
                    response = await SendUpstreamAsync(headers, effective.ToString(), ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TimeoutException && !ct.IsCancellationRequested)
                {
                    ReportNodeFailure(effective);
                    var alt = isPlaylistPath ? null : await TryFailoverAsync(headers, upstreamBase, ct);
                    if (alt is null)
                    {
                        _logger.LogWarning(ex, "Сегмент {Path} недоступен ни на одном известном узле.",
                            upstreamBase.AbsolutePath);
                        await WriteSimpleStatusAsync(stream, 502, "Bad Gateway", ct);
                        return;
                    }

                    response = alt;
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

                if (!isPlaylistPath && (int)response.StatusCode >= 400)
                {
                    var failedCode = (int)response.StatusCode;
                    var alt = await TryFailoverAsync(headers, upstreamBase, ct);
                    if (alt is not null)
                    {
                        response.Dispose();
                        response = alt;
                        contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Failover не удался: сегмент {Path} — узел {Host} ответил {Code}, других узлов не известно.",
                            upstreamBase.AbsolutePath, upstreamBase.Authority, failedCode);
                    }
                }

                var isPlaylist = isPlaylistPath
                    || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);

                using (response)
                {
                    if (isPlaylist)
                    {
                        await ProxyPlaylistAsync(stream, response, upstreamBase, headers, ct);
                    }
                    else
                    {
                        await ProxyBodyAsync(stream, response, effective, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down — expected
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Proxy connection failed.");
            }
        }
    }

    // Single upstream request; the 10 s cap covers the header phase only —
    // the body streams freely afterwards (a slow node may legitimately need
    // longer than the cap to deliver one segment)
    private async Task<HttpResponseMessage> SendUpstreamAsync(
        Dictionary<string, string> headers, string upstream, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, upstream);
        CopyRequestHeaders(headers, request);

        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
            // Headers received: disarm the cap so body streaming is not cut
            headerCts.CancelAfter(Timeout.InfiniteTimeSpan);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Узел {new Uri(upstream).Authority} не ответил заголовками за 10 с.");
        }
    }

    // Transparent segment failover: the playlist may rotate segments across
    // several CDN nodes with per-host auth tokens; a node that rejects (401
    // after a token rotation) or stalls must not stall playback when another
    // known node serves the same path with identical content
    private async Task<HttpResponseMessage?> TryFailoverAsync(
        Dictionary<string, string> headers, Uri upstream, CancellationToken ct)
    {
        string[] hosts;
        lock (_gate)
        {
            hosts = _nodeTokens.Keys
                .Where(h => !h.Equals(upstream.Authority, StringComparison.OrdinalIgnoreCase)
                    && (!_nodeHealth.TryGetValue(h, out var alt) || alt.DemotedUntil <= DateTime.UtcNow))
                .ToArray();
        }

        foreach (var host in hosts)
        {
            string token;
            lock (_gate)
            {
                token = _nodeTokens[host];
            }

            Uri altUrl;
            try
            {
                altUrl = new UriBuilder(upstream) { Host = host, Query = token }.Uri;
            }
            catch (UriFormatException)
            {
                continue;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var altResponse = await SendUpstreamAsync(headers, altUrl.ToString(), ct);
                if ((int)altResponse.StatusCode < 400)
                {
                    _logger.LogInformation(
                        "Failover: сегмент {Path} — узел {Host} не отдал его, резервный узел {Alt} ответил {Code} за {Ms:F0} мс.",
                        upstream.AbsolutePath, upstream.Authority, altUrl.Authority,
                        (int)altResponse.StatusCode, sw.Elapsed.TotalMilliseconds);
                    return altResponse;
                }

                altResponse.Dispose();
            }
            catch (Exception)
            {
                // Next candidate node
            }
        }

        return null;
    }

    // Redirect a segment request away from a demoted slow node
    private Uri EffectiveSegmentUrl(Uri upstream)
    {
        lock (_gate)
        {
            if (!_nodeHealth.TryGetValue(upstream.Authority, out var health) || health.DemotedUntil <= DateTime.UtcNow)
            {
                return upstream;
            }
        }

        return TryAlternateUrl(upstream) ?? upstream;
    }

    // URL of a known alternate node, skipping demoted ones; null if none
    private Uri? TryAlternateUrl(Uri upstream)
    {
        string? altHost;
        string token;
        lock (_gate)
        {
            altHost = _nodeTokens.Keys.FirstOrDefault(h =>
                !h.Equals(upstream.Authority, StringComparison.OrdinalIgnoreCase)
                && (!_nodeHealth.TryGetValue(h, out var alt) || alt.DemotedUntil <= DateTime.UtcNow));
            if (altHost is null)
            {
                return null;
            }

            token = _nodeTokens[altHost];
        }

        try
        {
            var redirected = new UriBuilder(upstream) { Host = altHost, Query = token }.Uri;
            if (DiagnosticsEnabled)
            {
                lock (_gate)
                {
                    if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastRedirectLog) < RedirectLogEvery)
                    {
                        return redirected;
                    }

                    _lastRedirectLog = System.Diagnostics.Stopwatch.GetTimestamp();
                }

                _logger.LogInformation(
                    "Сегменты идут через резервный узел {Alt} (основной {Host} пропущен).",
                    altHost, upstream.Authority);
            }

            return redirected;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    // Any delivery failure or slow segment demotes the node right away
    private void ReportNodeFailure(Uri node)
    {
        TimeSpan cooldown;
        lock (_gate)
        {
            _nodeHealth.TryGetValue(node.Authority, out var health);
            health.DemoteCount++;
            var minutes = NodeDemoteFor.TotalMinutes * Math.Pow(2, health.DemoteCount - 1);
            if (minutes > NodeDemoteMaxMinutes)
            {
                minutes = NodeDemoteMaxMinutes;
            }

            cooldown = TimeSpan.FromMinutes(minutes);
            health.DemotedUntil = DateTime.UtcNow + cooldown;
            _nodeHealth[node.Authority] = health;
        }

        _logger.LogWarning(
            "Узел {Host} тормозит — на {Min:F0} мин новые сегменты пойдут через резервные узлы.",
            node.Authority, cooldown.TotalMinutes);
    }

    private void ReportNodeSuccess(Uri node)
    {
        lock (_gate)
        {
            _nodeHealth.Remove(node.Authority);
        }
    }

    // Throttled log of segments delivered slower than ~2x real-time;
    // returns true when the segment was slow — a slow node deserves a
    // failure mark even though the segment eventually arrived
    private bool LogSlowSegment(TimeSpan elapsed, long bytes, Uri node)
    {
        if (elapsed < TimeSpan.FromSeconds(4) || bytes <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastSlowSegmentLog) < SlowSegmentLogEvery)
            {
                return true;
            }

            _lastSlowSegmentLog = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        if (!DiagnosticsEnabled)
        {
            return true;
        }

        _logger.LogInformation(
            "Медленный сегмент {Path}: {Mb:F1} МБ за {Sec:F1} с ({Mbps:F1} Мбит/с) с узла {Host}.",
            node.AbsolutePath, bytes / 1_048_576.0, elapsed.TotalSeconds,
            bytes * 8 / elapsed.TotalSeconds / 1_000_000.0, node.Authority);
        return true;
    }

    // Playlists carry the full node list with per-host tokens — remember them
    private void RememberNode(string url)
    {
        try
        {
            var uri = new Uri(url);
            if (string.IsNullOrEmpty(uri.Query))
            {
                return;
            }

            lock (_gate)
            {
                _nodeTokens[uri.Authority] = uri.Query.TrimStart('?');
            }
        }
        catch (UriFormatException)
        {
        }
    }

    private async Task<(string Path, Dictionary<string, string> Headers)> ReadRequestHeadAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var head = new StringBuilder();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (head.Length < 16_384)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (n <= 0)
            {
                return ("", headers);
            }

            head.Append(Encoding.ASCII.GetString(buffer, 0, n));
            var text = head.ToString();
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            var lines = text[..end].Split("\r\n");
            var path = lines[0].Split(' ').Length > 1 ? lines[0].Split(' ')[1] : "";
            foreach (var line in lines.Skip(1))
            {
                var sep = line.IndexOf(':');
                if (sep > 0)
                {
                    headers[line[..sep].Trim()] = line[(sep + 1)..].Trim();
                }
            }

            return (path, headers);
        }

        return ("", headers);
    }

    private static void CopyRequestHeaders(Dictionary<string, string> headers, HttpRequestMessage request)
    {

        foreach (var (name, value) in headers)
        {
            if (name.Equals("host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("accept-encoding", StringComparison.OrdinalIgnoreCase)
                || name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Equals("range", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("Range", value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
    }

    private async Task WriteTextPlaylistAsync(
        NetworkStream client, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        var head = new StringBuilder();
        head.Append("HTTP/1.1 200 OK\r\n");
        head.Append("Content-Type: application/vnd.apple.mpegurl\r\n");
        head.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");

        await client.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        await client.WriteAsync(bytes, ct);
    }

    private async Task ProxyPlaylistAsync(
        NetworkStream client, HttpResponseMessage response, Uri upstreamBase,
        Dictionary<string, string> headers, CancellationToken ct)
    {
        // Idle-capped read: ReadAsStringAsync would hang forever on a silent body
        await using var raw = await response.Content.ReadAsStreamAsync(ct);
        using var guarded = new IdleTimeoutStream(raw, BodyIdleTimeout);
        using var reader = new StreamReader(guarded);
        var body = await reader.ReadToEndAsync(ct);
        var segments = new List<string>();
        var rewritten = RewritePlaylist(body, upstreamBase, segments);

        // Pull announced segments now so their later request finds them cached
        if ((int)response.StatusCode < 400 && segments.Count > 0 && _cts is { IsCancellationRequested: false } proxyCts)
        {
            PrefetchSegments(headers, segments, _generation, proxyCts.Token);
        }

        if (DiagnosticsEnabled)
        {
            lock (_gate)
            {
                if (!_nodeCountLogged)
                {
                    _nodeCountLogged = true;
                    _logger.LogInformation("Плейлист: известно узлов CDN: {Count}.", _nodeTokens.Count);
                }
            }
        }
        var bytes = Encoding.UTF8.GetBytes(rewritten);

        var statusLine = response.StatusCode switch
        {
            HttpStatusCode.OK => "200 OK",
            _ => $"{(int)response.StatusCode} {response.StatusCode}",
        };

        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(statusLine).Append("\r\n");
        head.Append("Content-Type: ").Append(
            response.Content.Headers.ContentType?.ToString() ?? "application/vnd.apple.mpegurl").Append("\r\n");
        head.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");

        await client.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        await client.WriteAsync(bytes, ct);

        Count(bytes.LongLength);
    }

    // Spawn background downloads for the tail segments of the playlist
    private void PrefetchSegments(
        Dictionary<string, string> headers, List<string> urls, int generation, CancellationToken ct)
    {
        foreach (var url in urls.Skip(Math.Max(0, urls.Count - PrefetchSegmentCount)))
        {
            lock (_gate)
            {
                if (_segmentCache.ContainsKey(url) || _prefetchInFlight.ContainsKey(url))
                {
                    continue;
                }

                if (!_prefetchStartLogged)
                {
                    _prefetchStartLogged = true;
                    _logger.LogInformation(
                        "Prefetch: планирую фоновую загрузку сегментов ({Total} сегментов в плейлисте, качаю последние {N}).",
                        urls.Count, PrefetchSegmentCount);
                }

                _prefetchInFlight[url] = Task.Run(() => DownloadSegmentAsync(headers, url, generation, ct));
            }
        }
    }

    private async Task DownloadSegmentAsync(
        Dictionary<string, string> headers, string url, int generation, CancellationToken ct)
    {
        var baseUri = new Uri(url);
        try
        {
            var data = await FetchSegmentForCacheAsync(headers, baseUri, ct);
            if (data is null)
            {
                if (!_prefetchFailLogged && !ct.IsCancellationRequested)
                {
                    _prefetchFailLogged = true;
                    _logger.LogWarning(
                        "Prefetch: сегмент {Path} недоступен ни на одном узле — демуксер скачает сам.",
                        baseUri.AbsolutePath);
                }

                return;
            }

            lock (_gate)
            {
                if (generation == _generation && data.Value.Data.LongLength < MaxCacheBytes)
                {
                    TrimSegmentCache();
                    _segmentCache[url] = data.Value;
                    _cacheBytes += data.Value.Data.LongLength;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_prefetchFailLogged && !ct.IsCancellationRequested)
            {
                _prefetchFailLogged = true;
                _logger.LogWarning(ex,
                    "Prefetch: фоновая загрузка сегмента не удалась — демуксер скачает сам.");
            }
        }
        finally
        {
            lock (_gate)
            {
                _prefetchInFlight.Remove(url);
            }
        }
    }

    // One segment for the cache: pass 1 on the (demotion-aware) primary
    // node, pass 2 on an alternate — silent bodies and header failures
    // both fall through to the alternate
    private async Task<(byte[] Data, string ContentType)?> FetchSegmentForCacheAsync(
        Dictionary<string, string> headers, Uri baseUri, CancellationToken ct)
    {
        var primary = EffectiveSegmentUrl(baseUri);
        var first = await FetchSegmentBodyAsync(headers, primary, ct);
        if (first is not null || ct.IsCancellationRequested)
        {
            return first;
        }

        var alt = TryAlternateUrl(primary);
        if (alt is null)
        {
            return null;
        }

        return await FetchSegmentBodyAsync(headers, alt, ct);
    }

    // Fetch one segment from one exact host; null = this node failed
    private async Task<(byte[] Data, string ContentType)?> FetchSegmentBodyAsync(
        Dictionary<string, string> headers, Uri target, CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        try
        {
            response = await SendUpstreamAsync(headers, target.ToString(), ct);
            if ((int)response.StatusCode >= 400)
            {
                _logger.LogInformation(
                    "Узел {Host} ответил {Code} на сегмент {Path}.",
                    target.Authority, (int)response.StatusCode, target.AbsolutePath);
                response.Dispose();
                response = null;
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "video/mp2t";

            // Parallel ranged fetch for full-size segments; sequential fallback
            var length = response.Content.Headers.ContentLength;
            if (length is >= ParallelChunkMinBytes)
            {
                response.Dispose();
                response = null;
                var parallel = await TryParallelDownloadAsync(target, headers, length.Value, ct);
                if (parallel is not null)
                {
                    return (parallel, contentType);
                }

                // Parallel failure is a node failure even if the fallback works
                ReportNodeFailure(target);
                response = await SendUpstreamAsync(headers, target.ToString(), ct);
                if ((int)response.StatusCode >= 400)
                {
                    ReportNodeFailure(target);
                    response.Dispose();
                    return null;
                }
            }

            using (response)
            {
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                using var guarded = new IdleTimeoutStream(body, BodyIdleTimeout, MinSegmentWindowBytes, ThroughputWindow);
                using var buffered = new MemoryStream();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await guarded.CopyToAsync(buffered, 64 * 1024, ct);
                }
                catch (TimeoutException)
                {
                    ReportNodeFailure(target);
                    return null;
                }

                if (LogSlowSegment(sw.Elapsed, buffered.Length, target))
                {
                    ReportNodeFailure(target);
                }
                else
                {
                    ReportNodeSuccess(target);
                }

                return (buffered.ToArray(), contentType);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException && !ct.IsCancellationRequested)
        {
            response?.Dispose();
            ReportNodeFailure(target);
            return null;
        }
    }

    // One segment via ParallelChunkCount concurrent range requests; null on
    // any chunk failure — the caller falls back to a single-connection fetch
    private async Task<byte[]?> TryParallelDownloadAsync(
        Uri target, Dictionary<string, string> headers, long length, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var chunkSize = length / ParallelChunkCount;
        var buffers = new byte[ParallelChunkCount][];
        var tasks = new Task<byte[]>[ParallelChunkCount];
        try
        {
            for (var i = 0; i < ParallelChunkCount; i++)
            {
                var start = i * chunkSize;
                var end = i == ParallelChunkCount - 1 ? length - 1 : start + chunkSize - 1;
                tasks[i] = FetchRangeAsync(headers, target, start, end, ct);
            }

            for (var i = 0; i < ParallelChunkCount; i++)
            {
                buffers[i] = await tasks[i];
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException && !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Параллельная загрузка сегмента {Path} не удалась — одиночное соединение.", target.AbsolutePath);
            return null;
        }
        finally
        {
            // Sibling chunks may fault after the first failure or during
            // cancellation without a waiter — observe them or they hit the
            // finalizer thread as UnobservedTaskException
            foreach (var task in tasks)
            {
                _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        var data = new byte[length];
        var offset = 0;
        foreach (var chunk in buffers)
        {
            Buffer.BlockCopy(chunk, 0, data, offset, chunk.Length);
            offset += chunk.Length;
        }

        if (LogSlowSegment(sw.Elapsed, length, target))
        {
            ReportNodeFailure(target);
        }
        else
        {
            ReportNodeSuccess(target);
        }

        return data;
    }

    // One range chunk of a segment; a full-body 200 (no Content-Range) fails —
    // the node ignored Range and the caller must use a plain fetch
    private async Task<byte[]> FetchRangeAsync(
        Dictionary<string, string> headers, Uri target, long start, long end, CancellationToken ct)
    {
        var rangeHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        rangeHeaders.Remove("Range");

        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        CopyRequestHeaders(rangeHeaders, request);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

        using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            // Short cap: a chunk node slow to send headers stalls the whole
            // segment — fail fast into the sequential/failover path
            headerCts.CancelAfter(TimeSpan.FromSeconds(6));
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Convert the cap into TimeoutException so the caller's filters
                // catch it and the node gets its failure mark
                throw new TimeoutException(
                    $"Узел {target.Authority} не ответил заголовками за 6 с (кусок {start}-{end}).");
            }

            if ((int)response.StatusCode >= 400 || response.Content.Headers.ContentRange is null)
            {
                response.Dispose();
                throw new HttpRequestException($"Range {start}-{end}: узел {target.Authority} не отдал часть сегмента.");
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var guarded = new IdleTimeoutStream(
                body, BodyIdleTimeout, MinSegmentWindowBytes / ParallelChunkCount, ThroughputWindow);
            using var ms = new MemoryStream((int)(end - start + 1));
            await guarded.CopyToAsync(ms, 64 * 1024, ct);
            if (ms.Length != end - start + 1)
            {
                throw new HttpRequestException($"Range {start}-{end}: пришло {ms.Length} байт из {end - start + 1}.");
            }

            return ms.ToArray();
        }
    }

    // FIFO trim: live segments go stale fast, keep a bounded window
    private void TrimSegmentCache()
    {
        while (_segmentCache.Count > 0
            && (_segmentCache.Count >= MaxCachedSegments || _cacheBytes >= MaxCacheBytes))
        {
            var oldest = _segmentCache.First();
            _segmentCache.Remove(oldest.Key);
            _cacheBytes -= oldest.Value.Data.LongLength;
        }
    }

    // No header or "bytes=0-" means the whole body: both can be served from cache
    private static bool IsFullRangeRequest(Dictionary<string, string> headers)
    {
        return !headers.TryGetValue("Range", out var range)
            || range.Trim().Equals("bytes=0-", StringComparison.OrdinalIgnoreCase);
    }

    // Serve a prefetched segment, waiting out an in-flight pull if needed
    private async Task<bool> TryServeFromCacheAsync(NetworkStream client, string upstream, CancellationToken ct)
    {
        Task? inflight;
        lock (_gate)
        {
            if (_segmentCache.ContainsKey(upstream))
            {
                inflight = null;
            }
            else if (!_prefetchInFlight.TryGetValue(upstream, out inflight))
            {
                if (!_cacheMissLogged)
                {
                    _cacheMissLogged = true;
                    _logger.LogInformation(
                        "Кэш-промах: сегмент {Path} запрошен раньше, чем prefetch его объявил.",
                        new Uri(upstream).AbsolutePath);
                }

                return false;
            }

            if (!_cacheInflightLogged && inflight is not null)
            {
                _cacheInflightLogged = true;
                _logger.LogInformation("Кэш: сегмент {Path} запрошен во время фоновой загрузки — жду её.",
                    new Uri(upstream).AbsolutePath);
            }
        }

        if (inflight is not null)
        {
            try
            {
                await inflight.WaitAsync(ct);
            }
            catch (Exception)
            {
                // Cancelled or failed — fall back to the upstream path below
            }
        }

        byte[] data;
        string contentType;
        lock (_gate)
        {
            if (!_segmentCache.TryGetValue(upstream, out var entry))
            {
                return false;
            }

            data = entry.Data;
            contentType = entry.ContentType;
        }

        var head = new StringBuilder();
        head.Append("HTTP/1.1 200 OK\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(data.Length).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");
        await client.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        await client.WriteAsync(data, ct);
        Count(data.LongLength);

        if (!_prefetchHitLogged)
        {
            _prefetchHitLogged = true;
            _logger.LogInformation(
                "Prefetch работает: сегмент {Path} отдан из локального кэша (в кэше {Count} шт., {Mb:F1} МБ).",
                new Uri(upstream).AbsolutePath, _segmentCache.Count, _cacheBytes / (1024.0 * 1024.0));
        }

        return true;
    }


    private string RewritePlaylist(string body, Uri upstreamBase, List<string> segments)
    {
        var sb = new StringBuilder(body.Length + 1024);

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.Length == 0)
            {
                sb.AppendLine();
            }
            else if (line[0] == '#')
            {

                var uriPos = line.IndexOf("URI=\"", StringComparison.OrdinalIgnoreCase);
                if (uriPos >= 0)
                {
                    var start = uriPos + 5;
                    var end = line.IndexOf('"', start);
                    if (end > start)
                    {
                        var inner = line[start..end];
                        var absolute = Resolve(upstreamBase, inner);
                        if (absolute is not null)
                        {
                            RememberNode(absolute);
                        }
                        sb.AppendLine(line[..start] + (absolute == null ? inner : WrapUrl(absolute)) + line[end..]);
                        continue;
                    }
                }

                sb.AppendLine(line);
            }
            else
            {
                var absolute = Resolve(upstreamBase, line);
                if (absolute is not null)
                {
                    RememberNode(absolute);
                    // Only media segments, not variant/rendition playlists
                    if (!absolute.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                        && !absolute.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
                    {
                        segments.Add(absolute);
                    }
                }
                sb.AppendLine(absolute == null ? line : WrapUrl(absolute));
            }
        }

        return sb.ToString();
    }

    private static string? Resolve(Uri baseUrl, string maybeRelative)
    {
        try
        {
            var resolved = new Uri(baseUrl, maybeRelative);
            return resolved.Scheme is "http" or "https" ? resolved.ToString() : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private async Task ProxyBodyAsync(
        NetworkStream client, HttpResponseMessage response, Uri upstreamBase, CancellationToken ct)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append((int)response.StatusCode).Append(' ')
            .Append(response.ReasonPhrase ?? "OK").Append("\r\n");

        foreach (var (name, values) in response.Headers)
        {
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            head.Append(name).Append(": ").Append(string.Join(", ", values)).Append("\r\n");
        }

        foreach (var (name, values) in response.Content.Headers)
        {
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            head.Append(name).Append(": ").Append(string.Join(", ", values)).Append("\r\n");
        }

        var knownLength = response.Content.Headers.ContentLength;
        if (knownLength is > 0)
        {
            head.Append($"Content-Length: {knownLength}\r\n");
        }

        head.Append("Connection: close\r\n\r\n");

        await client.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);

        await using var upstream = await response.Content.ReadAsStreamAsync(ct);
        using var guarded = new IdleTimeoutStream(upstream, BodyIdleTimeout, MinSegmentWindowBytes, ThroughputWindow);
        var buffer = new byte[64 * 1024];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var total = 0L;
        try
        {
            while (true)
            {
                var n = await guarded.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (n <= 0)
                {
                    break;
                }

                await client.WriteAsync(buffer.AsMemory(0, n), ct);
                Count(n);
                total += n;
            }

            if (LogSlowSegment(sw.Elapsed, total, upstreamBase))
            {
                ReportNodeFailure(upstreamBase);
            }
            else
            {
                ReportNodeSuccess(upstreamBase);
            }
        }
        catch (TimeoutException)
        {
            ReportNodeFailure(upstreamBase);
            _logger.LogWarning(
                "Тело сегмента {Path} с узла {Host} застопорило (молчание >{Sec:F0} с или скорость ниже порога) — обрываю, плеер перезапросит.",
                upstreamBase.AbsolutePath, upstreamBase.Authority, BodyIdleTimeout.TotalSeconds);
            throw;
        }
    }

    private static async Task WriteSimpleStatusAsync(NetworkStream stream, int code, string reason, CancellationToken ct)
    {
        var head = $"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
    }

    private void Count(long bytes)
    {
        lock (_gate)
        {
            _totalBytes += bytes;
        }
    }

    private void Count(int bytes) => Count((long)bytes);

    private static string Encode(string url) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(url))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string? DecodePath(string path)
    {
        if (!path.StartsWith("/p/", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var rest = path[3..];


            var dot = rest.IndexOf('.');
            var b64 = (dot < 0 ? rest : rest[..dot]).Replace('-', '+').Replace('_', '/');
            switch (b64.Length % 4)
            {
                case 2: b64 += "=="; break;
                case 3: b64 += "="; break;
            }

            return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Stream wrapper: a read that sees no data for the idle cap — or a body
    // trickling below the throughput floor — throws TimeoutException instead
    // of hanging until the connection dies
    private sealed class IdleTimeoutStream : Stream
    {
        private readonly Stream _inner;
        private readonly TimeSpan _idle;
        private readonly long _minWindowBytes;
        private readonly TimeSpan _window;
        private long _windowStart;
        private long _windowBytes;

        public IdleTimeoutStream(Stream inner, TimeSpan idle, long minWindowBytes = 0, TimeSpan? window = null)
        {
            _inner = inner;
            _idle = idle;
            _minWindowBytes = minWindowBytes;
            _window = window ?? TimeSpan.Zero;
            _windowStart = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(_idle);
            try
            {
                var n = await _inner.ReadAsync(buffer, idle.Token);
                CheckThroughput(n);
                return n;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Upstream body idle for more than {_idle.TotalSeconds:F0} s.");
            }
        }

        private void CheckThroughput(int bytesRead)
        {
            if (_minWindowBytes <= 0 || bytesRead <= 0)
            {
                return;
            }

            _windowBytes += bytesRead;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_windowStart) < _window)
            {
                return;
            }

            if (_windowBytes < _minWindowBytes)
            {
                throw new TimeoutException(
                    $"Upstream body delivered only {_windowBytes} bytes in {_window.TotalSeconds:F0} s.");
            }

            _windowBytes = 0;
            _windowStart = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch (Exception)
        {

        }

        _http.Dispose();
    }
}
