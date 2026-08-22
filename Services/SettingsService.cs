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
            return Task.FromResult(JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings());
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
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить настройки.");
            throw;
        }
    }
}
