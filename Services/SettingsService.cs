using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using IptvPlayer.Models;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;

/// <summary>
/// Простое JSON-хранилище настроек в %LocalAppData%\IptvPlayer — тот же
/// каталог, где живут кэши (см. CacheService/PlaylistCacheService), поэтому
/// в MSIX-режиме (Debug) путь так же виртуализуется, как у них.
///
/// Раньше использовался Windows.Storage.ApplicationData.Current.LocalFolder,
/// но он требует package identity: в unpackaged-сборке (Release для
/// инсталятора Inno Setup) ApplicationData.Current == null и любое обращение
/// к настройкам падало бы исключением при запуске. Обычный файловый путь
/// работает в обоих режимах.
/// </summary>
public class SettingsService : ISettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly ILogger<SettingsService> _logger;

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
    }

    public Task<AppSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return Task.FromResult(new AppSettings());
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            UnprotectSecrets(settings);
            return Task.FromResult(settings);
        }
        catch (Exception ex)
        {
            // Повреждённый или недоступный файл настроек не должен ронять
            // приложение — откатываемся на настройки по умолчанию.
            _logger.LogWarning(ex, "Не удалось загрузить настройки — используются значения по умолчанию.");
            return Task.FromResult(new AppSettings());
        }
    }

    public Task SaveAsync(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var toSave = ProtectSecrets(settings);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(toSave, JsonOptions));
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить настройки.");
            throw;
        }
    }

    /// <summary>
    /// Шифрует секретные поля для записи на диск: ключ портала и URL
    /// плейлистов/EPG-источников (в m3u/EPG URL обычно зашиты username,
    /// password или токены). Работает с копией, переданный объект не меняется.
    /// </summary>
    private static AppSettings ProtectSecrets(AppSettings settings)
    {
        var clone = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        foreach (var playlist in clone.Playlists)
        {
            playlist.Url = SecretProtector.Protect(playlist.Url) ?? string.Empty;
            if (playlist.PortalKey != null)
            {
                playlist.PortalKey = SecretProtector.Protect(playlist.PortalKey);
            }
            foreach (var epg in playlist.EpgSources)
            {
                epg.Url = SecretProtector.Protect(epg.Url) ?? epg.Url;
            }
        }

        foreach (var epg in clone.EpgSources)
        {
            epg.Url = SecretProtector.Protect(epg.Url) ?? epg.Url;
        }

        return clone;
    }

    private static void UnprotectSecrets(AppSettings settings)
    {
        foreach (var playlist in settings.Playlists)
        {
            playlist.Url = SecretProtector.Unprotect(playlist.Url) ?? string.Empty;
            if (playlist.PortalKey != null)
            {
                playlist.PortalKey = SecretProtector.Unprotect(playlist.PortalKey);
            }
            foreach (var epg in playlist.EpgSources)
            {
                epg.Url = SecretProtector.Unprotect(epg.Url) ?? epg.Url;
            }
        }

        foreach (var epg in settings.EpgSources)
        {
            epg.Url = SecretProtector.Unprotect(epg.Url) ?? epg.Url;
        }
    }
}
