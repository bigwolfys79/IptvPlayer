using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using IptvPlayer.Models;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;


public class UpdateInfo
{
    public Version Version { get; init; } = new(0, 0);
    public string DownloadUrl { get; init; } = string.Empty;


    public string? Sha256 { get; init; }
}

public interface IUpdateService
{


    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);


    Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default);


    void StartInstaller(string setupPath);


    void RunInstallerAndExit(string setupPath);
}


public class UpdateService : IUpdateService
{
    private const string DefaultUpdateUrl = "https://api.github.com/repos/bigwolfys79/IptvPlayer/releases/latest";

    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ISettingsService _settingsService;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(ISettingsService settingsService, ILogger<UpdateService> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("IptvPlayer-UpdateCheck");
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            var url = string.IsNullOrWhiteSpace(settings.UpdateCheckUrl) ? DefaultUpdateUrl : settings.UpdateCheckUrl!;

            using var response = await Http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? versionText;
            string? downloadUrl;
            string? sha256 = null;

            if (root.TryGetProperty("tag_name", out var tag))
            {

                versionText = tag.GetString()?.TrimStart('v', 'V');
                downloadUrl = null;
                if (root.TryGetProperty("assets", out var assets) && assets.GetArrayLength() > 0)
                {
                    System.Text.Json.JsonElement? chosen = null;
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var assetName)
                            ? assetName.GetString()
                            : null;
                        if (string.IsNullOrEmpty(name) ||
                            !name.Contains("IptvPlayer-Setup-", StringComparison.OrdinalIgnoreCase) ||
                            !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        chosen = asset;
                        if (name.Contains("-x64", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }

                    if (chosen is { } assetElement)
                    {
                        if (assetElement.TryGetProperty("browser_download_url", out var assetUrl))
                        {
                            downloadUrl = assetUrl.GetString();
                        }

                        if (assetElement.TryGetProperty("digest", out var digest) &&
                            digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        {
                            sha256 = d["sha256:".Length..];
                        }
                    }
                    else
                    {
                        _logger.LogWarning("В релизе не найден подходящий ассет установщика (IptvPlayer-Setup-*.exe) — обновление не скачиваем.");
                    }
                }
            }
            else
            {

                versionText = root.TryGetProperty("version", out var v) ? v.GetString() : null;
                downloadUrl = root.TryGetProperty("url", out var u) ? u.GetString() : null;
            }

            var current = GetCurrentVersion();
            if (Version.TryParse(versionText, out var available) &&
                available > current &&
                !string.IsNullOrEmpty(downloadUrl))
            {
                return new UpdateInfo { Version = available, DownloadUrl = downloadUrl!, Sha256 = sha256 };
            }

            return null;
        }
        catch (Exception ex)
        {

            _logger.LogInformation(ex, "Автопроверка обновления не удалась (сеть недоступна?) — пропускаем.");
            return null;
        }
    }

    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"IptvPlayer-Setup-{update.Version}-x64.exe");

        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = System.IO.File.Create(path))
            {


                var buffer = new byte[81920];
                long copied = 0;
                int read;
                var lastReport = DateTimeOffset.MinValue;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    copied += read;

                    if (progress != null && totalBytes > 0 &&
                        (DateTimeOffset.UtcNow - lastReport).TotalMilliseconds >= 200)
                    {
                        lastReport = DateTimeOffset.UtcNow;
                        progress.Report(copied * 100.0 / totalBytes.Value);
                    }
                }
                if (progress != null && totalBytes > 0)
                {
                    progress.Report(100);
                }
            }
        }

        if (update.Sha256 is { } expected)
        {
            string actual;
            await using (var fileStream = System.IO.File.OpenRead(path))
            {
                using var sha = SHA256.Create();
                actual = Convert.ToHexString(await sha.ComputeHashAsync(fileStream, ct)).ToLowerInvariant();
            }

            if (!string.Equals(actual, expected.ToLowerInvariant(), StringComparison.Ordinal))
            {
                System.IO.File.Delete(path);
                throw new InvalidOperationException(L.T("Kontrolnaya_Summa_Ustanovshchika_Ne_Sovpala_Obnovlenie"));
            }
        }
        else
        {
            // No digest published by the release — do not block the update
            _logger.LogWarning("У релиза нет digest/sha256 — установка без проверки целостности.");
        }

        _logger.LogInformation("Обновление {Version} скачано: {Path} (sha256 {Sha}).",
            update.Version, path, update.Sha256 is null ? "не проверялась" : "совпала");
        return path;
    }

    public void StartInstaller(string setupPath)
    {
        _logger.LogInformation("Запуск тихой установки обновления: {Path}", setupPath);


        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(setupPath)
            {
                Arguments = "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES",
                UseShellExecute = true
            }
        };
        process.Start();
    }

    public void RunInstallerAndExit(string setupPath)
    {
        StartInstaller(setupPath);

        MainWindow.Instance?.Close();
    }

    internal static Version GetCurrentVersion()
    {
        try
        {

            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return new Version(v.Major, v.Minor, v.Build, v.Revision);
        }
        catch
        {

            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 9);
        }
    }
}
