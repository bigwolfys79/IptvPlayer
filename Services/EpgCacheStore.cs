using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MemoryPack;
using Serilog;

namespace IptvPlayer.Services;

/// <summary>
/// Быстрый дисковый кэш распарсенного XMLTV: MemoryPack (бинарная
/// сериализация кодогенерацией, без рефлексии) + Brotli (встроен в .NET,
/// отдельных нативных dll нет). Пришёл на смену JSON-кэшу CacheService:
/// 400k+ программ читаются за доли секунды вместо секунд, файл на диске
/// в разы меньше. Файлы лежат в том же каталоге %LocalAppData%\IptvPlayer\cache,
/// поэтому CacheService.Clear() (кнопка "Обновить EPG") удаляет и их тоже.
/// Вся работа с диском — из пула потоков: UI-поток не блокируется.
/// </summary>
public static class EpgCacheStore
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer", "cache");

    static EpgCacheStore()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
        }
        catch
        {
            // Нет прав/диска — останемся без дискового кэша, это не должно
            // ронять приложение (см. такую же политику в CacheService).
        }
    }

    private static string PathForKey(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(CacheDir, hash + ".mpck.br");
    }

    /// <summary>
    /// Читает кэш источника. null — промах (файла нет, битый, устаревший
    /// формат или другая ошибка чтения — вызывающий перекачает источник).
    /// </summary>
    public static async Task<CachedXmlTv?> ReadAsync(string key)
    {
        return await Task.Run(() =>
        {
            try
            {
                var path = PathForKey(key);
                if (!File.Exists(path))
                {
                    return null;
                }

                using var compressed = File.OpenRead(path);
                using var brotli = new BrotliStream(compressed, CompressionMode.Decompress);
                using var plain = new MemoryStream();
                brotli.CopyTo(plain);

                var cached = MemoryPackSerializer.Deserialize<CachedXmlTv>(plain.ToArray());
                if (cached is null || cached.FormatVersion != CachedXmlTv.CurrentFormatVersion)
                {
                    return null;
                }

                return cached;
            }
            catch (Exception ex)
            {
                // Битый/обрезанный файл (сбой записи, несовместимая версия) —
                // считаем промахом, как CacheService поступал с битым JSON.
                Log.Debug(ex, "Промах чтения дискового кэша EPG.");
                return null;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Записывает кэш источника (сжатие ~quality 4: быстрое, а текст XMLTV
    /// и так сжимается в разы). Ошибки проглатываются: отсутствие места на
    /// диске не должно ломать воспроизведение.
    /// </summary>
    public static Task WriteAsync(string key, CachedXmlTv value)
    {
        return Task.Run(() =>
        {
            try
            {
                var path = PathForKey(key);
                var tmp = path + ".tmp";
                var bytes = MemoryPackSerializer.Serialize(value);
                using (var file = File.Create(tmp))
                using (var brotli = new BrotliStream(file, CompressionLevel.Fastest))
                {
                    brotli.Write(bytes, 0, bytes.Length);
                }
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Ошибка записи дискового кэша EPG.");
            }
        });
    }
}
