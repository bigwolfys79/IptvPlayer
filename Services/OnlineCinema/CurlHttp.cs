using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IptvPlayer.Services;

public class CurlResult
{
    public int Status { get; init; }

    public string Body { get; init; } = string.Empty;
}


// HTTP engine backed by the system curl.exe (ships with Windows 10 1803+):
// Cloudflare scores the .NET TLS fingerprint (SChannel) as a bot and challenges
// those requests, while curl's TLS passes — verified live.
// Fully async so the UI thread never blocks on WaitForExit. No shell is
// involved: every token goes through ArgumentList; POST bodies are piped via
// stdin ("--data-binary @-").
public static class CurlHttp
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static Task<bool>? _available;

    public static Task<bool> GetAvailabilityAsync() =>
        _available ??= Task.Run(() =>
        {
            try
            {
                using var probe = new Process { StartInfo = BuildStartInfo("--version", postBody: false) };
                probe.Start();
                probe.WaitForExit(5000);
                return probe.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        });

    public static async Task<CurlResult?> GetAsync(string url, string? referer, bool iframe, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var headers = iframe
            ? new[]
            {
                $"Referer: {referer}", "Sec-Fetch-Dest: iframe", "Sec-Fetch-Mode: navigate",
                "Sec-Fetch-Site: cross-site"
            }
            : new[]
            {
                "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                "Accept-Language: ru-RU,ru;q=0.9", $"Referer: {referer}",
                "Upgrade-Insecure-Requests: 1", "Sec-Fetch-Dest: document", "Sec-Fetch-Mode: navigate",
                "Sec-Fetch-Site: same-origin", "Sec-Fetch-User: ?1"
            };

        return await RunAsync(url, headers, null, ct);
    }

    public static async Task<CurlResult?> PostJsonAsync(string url, string jsonBody, string referer, string origin,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return await RunAsync(url,
            new[] { "Accept: application/json, text/plain, */*", $"Referer: {referer}", $"Origin: {origin}" },
            jsonBody, ct);
    }

    // Form-urlencoded AJAX POST (DLE lightsearch: q=<query>)
    public static async Task<CurlResult?> PostFormAsync(string url, string formBody, string referer, string origin,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return await RunAsync(url,
            new[]
            {
                "Accept: application/json, text/plain, */*", "Content-Type: application/x-www-form-urlencoded; charset=UTF-8",
                "X-Requested-With: XMLHttpRequest", $"Referer: {referer}", $"Origin: {origin}"
            },
            formBody, ct);
    }

    private static ProcessStartInfo BuildStartInfo(string url, bool postBody)
    {
        var psi = new ProcessStartInfo("curl.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = postBody
        };
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add("--max-time");
        psi.ArgumentList.Add("30");
        psi.ArgumentList.Add("-A");
        psi.ArgumentList.Add(UserAgent);
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add("%{http_code}");
        if (postBody)
        {
            psi.ArgumentList.Add("--data-binary");
            psi.ArgumentList.Add("@-");
        }
        psi.ArgumentList.Add(url);
        return psi;
    }

    private static async Task<CurlResult?> RunAsync(string url, string[] headers, string? jsonBody,
        CancellationToken ct)
    {
        try
        {
            var outFile = Path.Combine(Path.GetTempPath(), $"iptv_{Guid.NewGuid():N}.out");
            var psi = BuildStartInfo(url, postBody: jsonBody != null);
            psi.ArgumentList.Insert(psi.ArgumentList.IndexOf("-s") + 1, "-o");
            psi.ArgumentList.Insert(psi.ArgumentList.IndexOf("-o") + 1, outFile);
            foreach (var header in headers)
            {
                psi.ArgumentList.Add("-H");
                psi.ArgumentList.Add(header);
            }

            using var process = Process.Start(psi);
            if (process == null)
            {
                return null;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(35));
            if (jsonBody != null)
            {
                await process.StandardInput.WriteAsync(jsonBody);
                process.StandardInput.Close();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(timeoutCts.Token);
            var statusText = await outputTask.WaitAsync(timeoutCts.Token);
            var status = int.TryParse(statusText.Trim(), out var code) ? code : 0;

            var body = File.Exists(outFile) ? await File.ReadAllTextAsync(outFile, ct) : string.Empty;
            try
            {
                if (File.Exists(outFile))
                {
                    File.Delete(outFile);
                }
            }
            catch (IOException)
            {
                // temp cleanup is best-effort
            }

            return new CurlResult { Status = status, Body = body };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
