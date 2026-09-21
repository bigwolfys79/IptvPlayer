using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MemoryPack;
using Serilog;

namespace IptvPlayer.Services;


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


            Log.Warning(ex, "Не удалось создать папку кэша EPG {Dir}.", CacheDir);
        }
    }


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
                    try
                    {
                        File.Delete(file);
                        Log.Information("Удалён осиротевший кэш EPG {File}.", Path.GetFileName(file));
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Не удалось удалить осиротевший кэш EPG {File}.", file);
                    }
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

            foreach (var tmp in Directory.EnumerateFiles(CacheDir, "*.tmp"))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Не удалось удалить временный файл кэша {File}.", tmp);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Очистка осиротевших кэшей EPG не удалась (не критично).");
        }
    }


    public static void ClearAll()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(CacheDir, "*.mpck.br"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Не удалось удалить кэш EPG {File}.", file);
                }
            }
        }
        catch (Exception ex)
        {


            Log.Debug(ex, "Очистка дискового кэша EPG не удалась (не критично).");
        }
    }


    public static string MergedKeyFor(IEnumerable<string> sourceUrls)
    {
        var joined = string.Join("|", sourceUrls);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
        return $"epgmerged:{hash}";
    }


    public static async Task<T?> ReadRecordAsync<T>(string key) where T : class
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

                return MemoryPackSerializer.Deserialize<T>(plain.ToArray());
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Промах чтения дискового кэша EPG ({Key}).", key);
                return null;
            }
        }).ConfigureAwait(false);
    }


    public static Task WriteRecordAsync<T>(string key, T value) where T : class
    {
        return Task.Run(() =>
        {
            var path = PathForKey(key);
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
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
                Log.Warning(ex, "Ошибка записи дискового кэша EPG ({Key}).", key);
                TryDeleteFile(tmp);
            }
        });
    }

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
            // Best effort
        }
    }

    private static string PathForKey(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(CacheDir, hash + ".mpck.br");
    }


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


                Log.Debug(ex, "Промах чтения дискового кэша EPG.");
                return null;
            }
        }).ConfigureAwait(false);
    }


    public static Task WriteAsync(string key, CachedXmlTv value)
    {
        return Task.Run(() =>
        {
            var path = PathForKey(key);
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
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
                TryDeleteFile(tmp);
            }
        });
    }
}
