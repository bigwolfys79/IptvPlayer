using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services
{
    /// <summary>
    /// Раньше кэш был чисто in-memory (Dictionary) — TTL в XmlTvService
    /// (CachedXmlTv.ExpiresAt, 3 часа) реально работал только в рамках одной
    /// сессии: при каждом перезапуске приложения EPG (505к+ программ с двух
    /// источников) скачивался и парсился заново, хотя формально ещё не
    /// устарел — кэшу просто негде было пережить рестарт. Теперь SetAsync
    /// дублирует значение на диск (JSON), а GetAsync при промахе в памяти
    /// читает с диска — идея (хэш ключа → файл) взята из EpgCacheService
    /// другого проекта, адаптирована под универсальный Get/SetAsync&lt;T&gt;
    /// вместо специализированного под один тип EPG-кэша.
    ///
    /// Путь — LocalApplicationData; в packaged (MSIX) приложении Win32 API
    /// сам резолвит его в виртуализированную AppData\Local\Packages\...,
    /// так что отдельных прав не требуется (аналогично файловому логу
    /// Serilog в App.xaml.cs).
    /// </summary>
    public class CacheService : ICacheService
    {
        private readonly Dictionary<string, object> _cache = new();
        private readonly string _cacheDir;
        private readonly ProcessSpeedMonitor? _speedMonitor;
        private readonly ILogger<CacheService>? _logger;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = false
        };

        public CacheService(ProcessSpeedMonitor? speedMonitor = null, ILogger<CacheService>? logger = null)
        {
            _speedMonitor = speedMonitor;
            _logger = logger;
            _cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IptvPlayer", "cache");
            try
            {
                Directory.CreateDirectory(_cacheDir);
            }
            catch (Exception ex)
            {
                // Нет прав/диска — просто останемся без дискового кэша,
                // in-memory часть продолжит работать как раньше.
                _logger?.LogWarning(ex, "Не удалось создать папку дискового кэша {Dir}.", _cacheDir);
            }
        }

        public async Task<T> GetAsync<T>(string key)
        {
            if (_cache.TryGetValue(key, out var value) && value is T typedValue)
            {
                return typedValue;
            }

            // Чтение с диска и десериализация (для XMLTV-кэша это сотни МБ
            // JSON и сотни тысяч объектов) раньше выполнялись синхронно на
            // вызывающем потоке — при старте приложения это UI-поток, и
            // интерфейс замерзал на секунды ещё до появления каналов. Уводим
            // в пул потоков; словарь _cache по-прежнему трогаем только на
            // вызывающем потоке.
            using var diskPause = _speedMonitor?.PauseScope();
            var fromDisk = await Task.Run(() => TryReadFromDisk<T>(key));
            if (fromDisk is not null)
            {
                _cache[key] = fromDisk;
                return fromDisk;
            }

            return default!;
        }

        public async Task SetAsync<T>(string key, T value)
        {
            _cache[key] = value!;

            // Сериализация + запись на диск — та же история: для большого
            // XMLTV-кэша это сотни МБ, синхронно на UI-потоке это секундный
            // фриз сразу после загрузки EPG.
            await Task.Run(() => TryWriteToDisk(key, value));
        }

        public Task RemoveAsync(string key)
        {
            _cache.Remove(key);
            TryDeleteFromDisk(key);
            return Task.CompletedTask;
        }

        public void Clear()
        {
            _cache.Clear();
            try
            {
                if (Directory.Exists(_cacheDir))
                {
                    Directory.Delete(_cacheDir, recursive: true);
                    Directory.CreateDirectory(_cacheDir);
                }
            }
            catch (Exception ex)
            {
                // Не критично — RefreshEPGAsync всё равно перезапишет файлы
                // новыми данными на следующем SetAsync.
                _logger?.LogWarning(ex, "Не удалось очистить папку дискового кэша {Dir}.", _cacheDir);
            }
        }

        private T? TryReadFromDisk<T>(string key)
        {
            try
            {
                var path = DiskPath(key);
                if (!File.Exists(path))
                {
                    return default;
                }

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<T>(json, JsonOpts);
            }
            catch (Exception ex)
            {
                // Битый/несовместимый (например, после апдейта модели EPGEntry)
                // файл кэша — считаем, что кэша нет, вызывающий код перекачает
                // источник и перезапишет файл свежими данными.
                _logger?.LogWarning(ex, "Не удалось прочитать файл кэша для ключа {Key} — считаем промахом.", key);
                return default;
            }
        }

        private void TryWriteToDisk<T>(string key, T value)
        {
            try
            {
                File.WriteAllText(DiskPath(key), JsonSerializer.Serialize(value, JsonOpts));
            }
            catch (Exception ex)
            {
                // Диск занят/нет прав — кэш останется только in-memory на
                // текущую сессию, приложение это не должно ронять.
                _logger?.LogWarning(ex, "Не удалось записать кэш на диск для ключа {Key}.", key);
            }
        }

        private void TryDeleteFromDisk(string key)
        {
            try
            {
                var path = DiskPath(key);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                // Не критично.
                _logger?.LogWarning(ex, "Не удалось удалить файл кэша для ключа {Key}.", key);
            }
        }

        /// <summary>
        /// Ключи вроде "xmltv:https://www.open-epg.com/files/russia3.xml"
        /// содержат символы, недопустимые в имени файла (":", "/") — хэшируем
        /// в безопасное имя, как в EpgCacheService другого проекта.
        /// </summary>
        private string DiskPath(string key)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            return Path.Combine(_cacheDir, hash + ".json");
        }
    }
}