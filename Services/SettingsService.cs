using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using IptvPlayer.Models;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;


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

    // Shared snapshot for synchronous readers (window placement, tray options)
    public static AppSettings? Current { get; private set; }

    // Outcome of the latest LoadAsync — bootstrap load runs before Serilog exists
    public static string? LastLoadNotice { get; private set; }

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
    }

        public async Task<AppSettings> LoadAsync()
        {
            try
            {
                if (_cached != null)
                {
                    return _cached;
                }

                // Another instance (e.g. App bootstrap) already loaded settings
                if (Current != null)
                {
                    _cached = Current;
                    return _cached;
                }

                if (!File.Exists(SettingsPath))
                {
                    LastLoadNotice = "settings.json отсутствует — использованы настройки по умолчанию.";
                    _cached = new AppSettings();
                    Current = _cached;
                    return _cached;
                }

                var json = await File.ReadAllTextAsync(SettingsPath).ConfigureAwait(false);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                UnprotectSecrets(settings);
                _cached = settings;
                Current = _cached;
                LastLoadNotice = null;
                return _cached;
            }
            catch (Exception ex)
            {

                _logger.LogWarning(ex, "Не удалось загрузить настройки из {Path} — файл сохранён как *.corrupt, пробуем резервную копию.", SettingsPath);
                TrySnapshotCorruptFile();
                var restored = TryLoadBackup();
                if (restored != null)
                {
                    _logger.LogWarning("Настройки восстановлены из {Backup}.", restored.Value.path);
                    LastLoadNotice = $"Настройки не читаются ({ex.GetType().Name}: {ex.Message}) — восстановлены из {restored.Value.path}.";
                    _cached = restored.Value.settings;
                    Current = _cached;
                    return _cached;
                }

                LastLoadNotice = $"Настройки не читаются ({ex.GetType().Name}: {ex.Message}) — резервной копии нет, использованы значения по умолчанию.";
                _logger.LogWarning("Резервной копии нет — используются значения по умолчанию (файл на диске не перезаписывается до первой успешной загрузки).");
                _cached = new AppSettings();
                Current = _cached;
                return _cached;
            }
        }


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

                var tempPath = SettingsPath + ".tmp";
                await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);

                try
                {
                    for (var attempt = 1; ; attempt++)
                    {
                        try
                        {
                            if (File.Exists(SettingsPath))
                            {
                                await Task.Run(() => File.Copy(SettingsPath, SettingsPath + ".prev", overwrite: true)).ConfigureAwait(false);
                            }
                            File.Move(tempPath, SettingsPath, overwrite: true);
                            break;
                        }
                        catch (IOException) when (attempt < 5)
                        {
                            await Task.Delay(200 * attempt).ConfigureAwait(false);
                        }
                    }
                }
                catch
                {
                    // Remove leftover temp file when all move attempts failed
                    TryDeleteFile(tempPath);
                    throw;
                }

                _cached = settings;
                Current = settings;
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

    // Best-effort temp file cleanup
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore — cleanup is best effort
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

    private void UnprotectSecrets(AppSettings settings)
    {
        foreach (var playlist in settings.Playlists)
        {
            playlist.Url = UnprotectOrKeep(playlist.Url);
            if (playlist.PortalKey != null)
            {
                playlist.PortalKey = UnprotectOrKeep(playlist.PortalKey);
            }
            foreach (var epg in playlist.EpgSources)
            {
                epg.Url = UnprotectOrKeep(epg.Url);
            }
        }

        foreach (var epg in settings.EpgSources)
        {
            epg.Url = UnprotectOrKeep(epg.Url);
        }
    }

    // Keep original dpapi: value on failure — never write null/empty over it
    private string? UnprotectKeepOriginal(string? value)
    {
        var result = SecretProtector.Unprotect(value);
        if (result == null && value != null)
        {
            _logger.LogWarning("Расшифровка защищённого значения не удалась — исходное значение сохранено без изменений.");
            return value;
        }

        return result;
    }

    // Non-null wrapper for direct field assignment
    private string UnprotectOrKeep(string? value) => UnprotectKeepOriginal(value) ?? string.Empty;

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
