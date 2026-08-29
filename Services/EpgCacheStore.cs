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
/// отдельных нативных dll нет). Пришёл на смену прежнему JSON-кэшу:
/// 400k+ программ читаются за доли секунды вместо секунд, файл на диске
/// в разы меньше. Файлы лежат в %LocalAppData%\IptvPlayer\cache,
/// ClearAll() (кнопка "Обновить EPG") удаляет их все.
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
        catch (Exception ex)
        {
            // Нет прав/диска — останемся без дискового кэша, это не должно
            // ронять приложение (та же политика, что и в других сервисах).
            Log.Warning(ex, "Не удалось создать папку кэша EPG {Dir}.", CacheDir);
        }
    }

    /// <summary>
    /// Удаляет кэш-файлы осиротевших источников: после правки списка EPG
    /// источников старые .mpck.br (десятки мегабайт каждый) иначе остаются
    /// на диске навсегда. Вызывается при загрузке EPG; ключи — те же, что
    /// в ReadAsync/WriteAsync (в XmlTvService это "xmltv:{url}").
    /// Заодно подчищает легаси *.json от давно удалённого JSON-кэша EPG;
    /// других .json в этом каталоге нет.
    /// </summary>
    public static void CleanupOrphans(IEnumerable<string> liveKeys)
    {
        try
        {
            var live = liveKeys
                .Select(k => Path.GetFileName(PathForKey(k)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(CacheDir, "*.mpck.br"))
            {
                if (!live.Contains(Path.GetFileName(file)))
                {
                    File.Delete(file);
                    Log.Information("Удалён осиротевший кэш EPG {File}.", Path.GetFileName(file));
                }
            }

            foreach (var legacy in Directory.EnumerateFiles(CacheDir, "*.json"))
            {
                try
                {
                    File.Delete(legacy);
                    Log.Information("Удалён устаревший JSON-кэш EPG {File}.", Path.GetFileName(legacy));
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Не удалось удалить устаревший JSON-кэш {File}.", legacy);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Очистка осиротевших кэшей EPG не удалась (не критично).");
        }
    }

    /// <summary>
    /// Удаляет все .mpck.br файлы — кнопка "Обновить EPG" должна заставить
    /// источники перекачаться по сети, а не взять их с диска.
    /// </summary>
    public static void ClearAll()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(CacheDir, "*.mpck.br"))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            // Не критично: с "Обновить EPG" источники в любом случае
            // перекачаются — TTL-проверка пройдёт мимо свежего файла.
            Log.Debug(ex, "Очистка дискового кэша EPG не удалась (не критично).");
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
                // считаем промахом, как прежний JSON-кэш поступал с битым файлом.
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
