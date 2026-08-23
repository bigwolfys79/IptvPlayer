using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using IptvPlayer.Models;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;

/// <summary>Найденное обновление: версия, ссылка на установщик и контрольная сумма (если релиз её отдаёт).</summary>
public class UpdateInfo
{
    public Version Version { get; init; } = new(0, 0);
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>SHA256 установщика из GitHub API (assets[].digest, вид "sha256:hex"). null — источник сумму не отдал.</summary>
    public string? Sha256 { get; init; }
}

public interface IUpdateService
{
    /// <summary>
    /// Проверяет обновление по GitHub Releases (или UpdateCheckUrl из настроек).
    /// Возвращает UpdateInfo, если доступная версия новее текущей; null —
    /// обновления нет. Ошибки сети не бросает — null и запись в лог.
    /// </summary>
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);

    /// <summary>
    /// Скачивает установщик во временную папку и проверяет контрольную сумму
    /// (если известна). Возвращает путь к файлу; при несовпадении суммы файл
    /// удаляется и бросается исключение — устанавливать нельзя.
    /// </summary>
    Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Запускает установщик в тихом режиме и закрывает приложение. Установщик
    /// ставит поверх (старая версия остаётся рабочей при сбое), после установки
    /// запускает приложение (запись [Run] в .iss для тихого режима). UAC
    /// подтверждение остаётся: установка идёт в Program Files.
    /// </summary>
    void RunInstallerAndExit(string setupPath);
}

/// <summary>
/// Полуавтоматическое обновление: фоновая проверка (не чаще раза в сутки по
/// вызову), скачивание установщика с проверкой SHA256 и тихая установка поверх
/// после согласия пользователя. Логика разбора ответа GitHub API та же, что в
/// ручной проверке «О программе» (AboutDialog), вынесена сюда для переиспользования.
/// </summary>
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
                // Формат GitHub API /releases/latest.
                versionText = tag.GetString()?.TrimStart('v', 'V');
                downloadUrl = null;
                if (root.TryGetProperty("assets", out var assets) && assets.GetArrayLength() > 0)
                {
                    var asset = assets[0];
                    if (asset.TryGetProperty("browser_download_url", out var assetUrl))
                    {
                        downloadUrl = assetUrl.GetString();
                    }

                    if (asset.TryGetProperty("digest", out var digest) &&
                        digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        sha256 = d["sha256:".Length..];
                    }
                }

                downloadUrl ??= root.TryGetProperty("html_url", out var html) ? html.GetString() : null;
            }
            else
            {
                // Простой формат {"version": "...", "url": "..."}.
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
            // Проверка обновления не должна никак мешать работе — тихо в лог.
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
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = System.IO.File.Create(path))
            {
                await source.CopyToAsync(target, 81920, ct);
            }
        }

        // Контрольная сумма: GitHub API отдаёт digest не для всех релизов —
        // без суммы пропускаем (HTTPS), с суммой несовпадение = не устанавливать.
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
                throw new InvalidOperationException(L.T(
                    "Контрольная сумма установщика не совпала — обновление отменено.",
                    "Installer checksum mismatch — update cancelled."));
            }
        }

        _logger.LogInformation("Обновление {Version} скачано: {Path} (sha256 {Sha}).",
            update.Version, path, update.Sha256 is null ? "не проверялась" : "совпала");
        return path;
    }

    public void RunInstallerAndExit(string setupPath)
    {
        _logger.LogInformation("Запуск тихой установки обновления и выход из приложения: {Path}", setupPath);

        // UseShellExecute — установщику нужен UAC-подъём (Program Files).
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(setupPath)
            {
                Arguments = "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES",
                UseShellExecute = true
            }
        };
        process.Start();

        // Полный выход приложения: освобождает файлы до того, как установщик
        // дойдёт до копирования (Inno сам ждёт/повторяет при занятых файлах).
        MainWindow.Instance?.Close();
    }

    internal static Version GetCurrentVersion()
    {
        try
        {
            // MSIX-сборка — версия пакета.
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return new Version(v.Major, v.Minor, v.Build, v.Revision);
        }
        catch
        {
            // Unpackaged (Inno Setup) — версия сборки.
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 9);
        }
    }
}
