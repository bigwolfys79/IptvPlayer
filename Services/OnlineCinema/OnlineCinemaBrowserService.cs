using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace IptvPlayer.Services;

// Hidden WebView2 host for online-cinema sites: persistent profile (keeps cf_clearance),
// off-screen window, api/playlist/load interception. All public methods must be called
// on the UI thread — the WebView2 control requires it.
public class OnlineCinemaBrowserService : IDisposable
{
    private static readonly Regex ChallengeTitleRegex =
        new(@"just a moment|один момент|attention required", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private Window? _window;
    private WebView2? _webView;
    private CoreWebView2? _core;

    public class StreamHit
    {
        public string Url { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;
    }

    private readonly object _playlistGate = new();
    private readonly List<StreamHit> _streamHits = new();

    public bool IsInitialized => _core != null;

    public async Task InitializeAsync()
    {
        if (_core != null)
        {
            return;
        }

        _window = new Window { Title = "IptvPlayer" };
        _window.AppWindow.IsShownInSwitchers = false;
        _window.AppWindow.Resize(new SizeInt32(1024, 720));
        var grid = new Grid();
        _webView = new WebView2();
        grid.Children.Add(_webView);
        _window.Content = grid;

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer", "WebView2");
        var env = await CoreWebView2Environment.CreateWithOptionsAsync(
            null, userDataFolder, new CoreWebView2EnvironmentOptions());
        await _webView.EnsureCoreWebView2Async(env);
        _core = _webView.CoreWebView2;
        _core.WebMessageReceived += OnWebMessageReceived;
        await _core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);

        // Diagnostics: trace embed-related traffic (embed iframes, playlist api, streams)
        _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        _core.WebResourceRequested += OnWebResourceRequested;
        _core.WebResourceResponseReceived += OnWebResourceResponseReceived;

        _window.Activate();
        MoveOffScreen();
        HideFromTaskbar();
    }

    private void MoveOffScreen()
    {
        _window?.AppWindow.Move(new PointInt32(-32000, 0));
    }

    // WS_EX_TOOLWINDOW removes the taskbar button — the whole browsing session
    // must stay invisible to the user
    private void HideFromTaskbar()
    {
        if (_window == null)
        {
            return;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
    }

    // Trusted mouse click at CSS viewport coordinates via the DevTools protocol —
    // the same mechanism Playwright uses. No window visibility, no cursor
    // movement: the browser window stays off-screen and hidden from the taskbar
    public async Task ClickAtAsync(double cssX, double cssY)
    {
        if (_core == null)
        {
            throw new InvalidOperationException("Browser is not initialized");
        }

        var x = cssX.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var y = cssY.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await _core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
            $"{{\"type\":\"mousePressed\",\"x\":{x},\"y\":{y},\"button\":\"left\",\"clickCount\":1}}");
        await Task.Delay(60);
        await _core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
            $"{{\"type\":\"mouseReleased\",\"x\":{x},\"y\":{y},\"button\":\"left\",\"clickCount\":1}}");
    }

    // Navigate and wait for load; waits out the Cloudflare challenge silently
    // (no window surfacing — the whole session stays invisible). A challenge
    // that does not clear on its own fails the navigation
    public async Task<bool> NavigateAsync(string url, CancellationToken ct = default)
    {
        if (_core == null)
        {
            throw new InvalidOperationException("Browser is not initialized");
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult(e.IsSuccess);
        _core.NavigationCompleted += Handler;
        try
        {
            _core.Navigate(url);
            var loaded = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(2), ct);
            if (!loaded)
            {
                return false;
            }
        }
        finally
        {
            _core.NavigationCompleted -= Handler;
        }

        return await WaitForChallengeAsync(ct);
    }

    private async Task<bool> WaitForChallengeAsync(CancellationToken ct)
    {
        if (_core == null)
        {
            return false;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var title = await RunScriptAsync("document.title") ?? string.Empty;
            if (!ChallengeTitleRegex.IsMatch(title))
            {
                return true;
            }

            await Task.Delay(2000, ct).ConfigureAwait(true);
        }

        return false;
    }

    // Executes JS and returns the scalar/string result (ExecuteScriptAsync JSON-decoded)
    public async Task<string?> RunScriptAsync(string script, CancellationToken ct = default)
    {
        if (_core == null)
        {
            throw new InvalidOperationException("Browser is not initialized");
        }

        var json = await _core.ExecuteScriptAsync(script).AsTask(ct);
        return JsonSerializer.Deserialize<string>(json);
    }

    // Executes JS returning an object and deserializes it once
    public async Task<JsonElement> RunScriptJsonAsync(string script, CancellationToken ct = default)
    {
        if (_core == null)
        {
            throw new InvalidOperationException("Browser is not initialized");
        }

        var json = await _core.ExecuteScriptAsync(script).AsTask(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public void ClearPlaylistResponses()
    {
        lock (_playlistGate)
        {
            _streamHits.Clear();
        }
    }

    // Waits for the first stream hit collected since ClearPlaylistResponses.
    // Hits come from the bridge: any response whose body mentions m3u8, plus the
    // classic api/playlist/load JSON — covers all embed providers
    public async Task<StreamHit?> WaitForStreamHitAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            StreamHit? hit;
            lock (_playlistGate)
            {
                hit = _streamHits.Count > 0 ? _streamHits[0] : null;
                if (hit != null)
                {
                    _streamHits.RemoveAt(0);
                }
            }

            if (hit != null)
            {
                return hit;
            }

            await Task.Delay(500, ct).ConfigureAwait(true);
        }

        return null;
    }

    // CDN segment hosts are excluded — embed providers and their api matter
    private static bool IsInterestingUrl(string url) =>
        url.Contains("cinemar", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("nextembed", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("playlist", StringComparison.OrdinalIgnoreCase);

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var url = e.Request?.Uri ?? string.Empty;
        if (IsInterestingUrl(url))
        {
            Serilog.Log.Information("Онлайн-кинотеатр [req]: {Url}", url);
        }
    }

    // Browser-level interception: reads api/playlist/json and m3u8 responses from
    // ALL frames. The in-page fetch/XHR bridge does not fire inside cross-origin
    // iframes (verified live), so this is the reliable capture path
    private async void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            var url = e.Request?.Uri ?? string.Empty;
            var response = e.Response;
            if (response == null)
            {
                return;
            }

            var contentType = GetHeader(response.Headers, "content-type");
            var interesting = url.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ||
                              url.Contains("playlist", StringComparison.OrdinalIgnoreCase) ||
                              (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false) ||
                              (contentType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ?? false);
            if (!interesting)
            {
                return;
            }

            using var content = await response.GetContentAsync();
            if (content == null)
            {
                return;
            }

            using var stream = content.AsStreamForRead();
            // Full body up to a cap — a single ReadAsync may return a partial chunk,
            // and a truncated JSON is unparseable
            var buffer = new byte[512 * 1024];
            var total = 0;
            int read;
            while (total < buffer.Length &&
                   (read = await stream.ReadAsync(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            var body = System.Text.Encoding.UTF8.GetString(buffer, 0, total);
            if (body.Contains("m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var hit = new StreamHit { Url = url, Body = body };
                lock (_playlistGate)
                {
                    _streamHits.Add(hit);
                }

                Serilog.Log.Information("Онлайн-кинотеатр [hit]: {Url}", url);
            }
        }
        catch (Exception)
        {
            // Diagnostics/capture only — response read failures are not fatal
        }
    }

    private static string? GetHeader(CoreWebView2HttpResponseHeaders? headers, string name)
    {
        if (headers == null)
        {
            return null;
        }

        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) &&
                type.GetString() == "stream" &&
                root.TryGetProperty("url", out var url))
            {
                var hit = new StreamHit
                {
                    Url = url.GetString() ?? string.Empty,
                    Body = root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty
                };
                lock (_playlistGate)
                {
                    _streamHits.Add(hit);
                }
            }
        }
        catch (JsonException)
        {
            // Non-bridge messages are ignored
        }
    }

    // Injected into every document/frame before page scripts run: wraps fetch and
    // XMLHttpRequest so any response that carries an m3u8 (its URL plus a body
    // snippet) reaches us via postMessage — provider-agnostic stream detection
    private const string BridgeScript = @"
(function () {
    if (window.__iptvBridge) return;
    window.__iptvBridge = true;
    var post = function (url, body) {
        try { window.chrome.webview.postMessage({ type: 'stream', url: String(url), body: String(body).slice(0, 400) }); } catch (e) {}
    };
    var isPlaylist = function (u) { return String(u).indexOf('api/playlist/load') !== -1; };
    var looksLikeStream = function (t) { return t && t.indexOf('m3u8') !== -1; };
    var of = window.fetch;
    if (of) {
        window.fetch = function () {
            var p = of.apply(this, arguments);
            try {
                var u = arguments[0];
                u = u && (typeof u === 'string' ? u : u.url);
                p.then(function (r) {
                    r.clone().text().then(function (t) {
                        if (looksLikeStream(t) || isPlaylist(u)) post(u, t);
                    }).catch(function () {});
                }).catch(function () {});
            } catch (e) {}
            return p;
        };
    }
    var oo = XMLHttpRequest.prototype.open;
    var os = XMLHttpRequest.prototype.send;
    XMLHttpRequest.prototype.open = function (m, u) { this.__iptvUrl = u; return oo.apply(this, arguments); };
    XMLHttpRequest.prototype.send = function () {
        var xhr = this;
        xhr.addEventListener('load', function () {
            try {
                var t = xhr.responseText;
                if ((t && looksLikeStream(t)) || isPlaylist(xhr.__iptvUrl)) post(xhr.__iptvUrl, t);
            } catch (e) {}
        });
        return os.apply(this, arguments);
    };
})();";

    public void Dispose()
    {
        if (_core != null)
        {
            _core.WebMessageReceived -= OnWebMessageReceived;
        }

        _window?.Close();
        _core = null;
        _webView = null;
        _window = null;
    }


    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
}
