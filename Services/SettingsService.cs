using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using IptvPlayer.Models;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;

/// <summary>
/// Простое JSON-хранилище настроек в %LocalAppData%\IptvPlayer — тот же
/// каталог, где живут кэши (см. EpgCacheStore/PlaylistDatabaseService), поэтому
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
        WriteIndented = true,
        Converters = { new NullableDateTimeConverter() }
    };

    private readonly ILogger<SettingsService> _logger;
    private AppSettings? _cached;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
    }

        public Task<AppSettings> LoadAsync()
        {
            try
            {
                if (_cached != null)
                {
                    return Task.FromResult(_cached);
                }

                if (!File.Exists(SettingsPath))
                {
                    _cached = new AppSettings();
                    return Task.FromResult(_cached);
                }

                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                UnprotectSecrets(settings);
                _cached = settings;
                return Task.FromResult(settings);
            }
            catch (Exception ex)
            {
                // Битый/залоченный файл нельзя молча заменять дефолтами: так
                // терялись все плейлисты и источники (затирание было замечено
                // дважды за день). Сохраняем виновника с меткой — его можно
                // разобрать вручную — и пробуем предыдущую сохранённую копию.
                _logger.LogWarning(ex, "Не удалось загрузить настройки из {Path} — файл сохранён как *.corrupt, пробуем резервную копию.", SettingsPath);
                TrySnapshotCorruptFile();
                var restored = TryLoadBackup();
                if (restored != null)
                {
                    _logger.LogWarning("Настройки восстановлены из {Backup}.", restored.Value.path);
                    _cached = restored.Value.settings;
                    return Task.FromResult(_cached);
                }

                _logger.LogWarning("Резервной копии нет — используются значения по умолчанию (файл на диске не перезаписывается до первой успешной загрузки).");
                _cached = new AppSettings();
                return Task.FromResult(_cached);
            }
        }

        /// <summary>Битый файл переименовывается, а не удаляется — данные можно вытащить.</summary>
        private void TrySnapshotCorruptFile()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    File.Move(SettingsPath, SettingsPath + $".corrupt-{stamp}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось переименовать битый settings.json.");
            }
        }

        /// <summary>Пытается прочитать settings.json.prev (прошлую успешную запись).</summary>
        private (string path, AppSettings settings)? TryLoadBackup()
        {
            try
            {
                var backupPath = SettingsPath + ".prev";
                if (!File.Exists(backupPath)) return null;
                var json = File.ReadAllText(backupPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings == null) return null;
                UnprotectSecrets(settings);
                return (backupPath, settings);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Резервная копия настроек не читается.");
                return null;
            }
        }

        public async Task SaveAsync(AppSettings settings)
        {
            await _saveLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var toSave = ProtectSecrets(settings);
                var json = JsonSerializer.Serialize(toSave, JsonOptions);

                // Атомарная запись: сначала во временный файл, затем замена.
                // Прямая запись в settings.json при сбое процесса/блокировке
                // другим экземпляром (приложение живёт в трее) оставляла
                // битый JSON, который при следующем старте заменялся
                // дефолтами — с потерей всех плейлистов.
                var tempPath = SettingsPath + ".tmp";
                await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);

                // Повторы: параллельный экземпляр (сворачивание в трей)
                // может удерживать файл во время замены — даём ему время.
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(SettingsPath))
                        {
                            // Прошлая успешная запись — страховка от порчи нового.
                            File.Copy(SettingsPath, SettingsPath + ".prev", overwrite: true);
                        }
                        File.Move(tempPath, SettingsPath, overwrite: true);
                        break;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        await Task.Delay(200 * attempt).ConfigureAwait(false);
                    }
                }

                _cached = settings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось сохранить настройки.");
                throw;
            }
            finally
            {
                _saveLock.Release();
            }
        }

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

    private sealed class NullableDateTimeConverter : JsonConverter<DateTime?>
    {
        private static readonly string[] FallbackFormats =
        {
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "dd.MM.yyyy HH:mm:ss"
        };

        public override DateTime? Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                var raw = reader.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    return parsed;
                }

                foreach (var format in FallbackFormats)
                {
                    if (DateTime.TryParseExact(raw, format, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var exact))
                    {
                        return exact;
                    }
                }

                return null;
            }

            return null;
        }

        public override void Write(
            Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value is { } date)
            {
                writer.WriteStringValue(date);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}
