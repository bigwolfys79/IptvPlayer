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

    private readonly HttpClient _http;
    private readonly Microsoft.Extensions.Logging.ILogger<LocalStreamProxy> _logger;
    private readonly object _gate = new();
    private readonly Queue<double> _window = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private long _totalBytes;
    private long _lastSampleBytes;
    private readonly System.Diagnostics.Stopwatch _sampleClock = System.Diagnostics.Stopwatch.StartNew();
    private string _baseUrl = "";

    public LocalStreamProxy(Microsoft.Extensions.Logging.ILogger<LocalStreamProxy> logger)
    {
        _logger = logger;
        _http = new HttpClient(new SocketsHttpHandler
        {

            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public bool IsRunning => _listener is not null;


    public string WrapUrl(string upstreamUrl)
    {
        if (!IsRunning && !TryStart())
        {
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

                var upstream = DecodePath(path);
                if (upstream is null)
                {
                    await WriteSimpleStatusAsync(stream, 404, "Not Found", ct);
                    return;
                }

                var upstreamBase = new Uri(upstream);

                using var request = new HttpRequestMessage(HttpMethod.Get, upstream);
                CopyRequestHeaders(headers, request);

                using var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                var isPlaylist = contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                    || upstreamBase.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                    || upstreamBase.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);

                if (isPlaylist)
                {
                    await ProxyPlaylistAsync(stream, response, upstreamBase, ct);
                }
                else
                {
                    await ProxyBodyAsync(stream, response, ct);
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

    private async Task ProxyPlaylistAsync(
        NetworkStream client, HttpResponseMessage response, Uri upstreamBase, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var rewritten = RewritePlaylist(body, upstreamBase);
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


    private string RewritePlaylist(string body, Uri upstreamBase)
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
                        sb.AppendLine(line[..start] + (absolute == null ? inner : WrapUrl(absolute)) + line[end..]);
                        continue;
                    }
                }

                sb.AppendLine(line);
            }
            else
            {
                var absolute = Resolve(upstreamBase, line);
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

    private async Task ProxyBodyAsync(NetworkStream client, HttpResponseMessage response, CancellationToken ct)
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
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var n = await upstream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (n <= 0)
            {
                break;
            }

            await client.WriteAsync(buffer.AsMemory(0, n), ct);
            Count(n);
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
