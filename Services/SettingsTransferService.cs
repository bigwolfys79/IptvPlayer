using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using IptvPlayer.Models;

namespace IptvPlayer.Services;

/// <summary>
/// Перенос настроек между машинами: экспорт в файл, защищённый паролем
/// (AES-GCM, ключ выводится PBKDF2-SHA256 из пароля и случайной соли), и
/// импорт с выбором режима — заменить всё или добавить только плейлисты.
///
/// Нужен потому, что секреты в settings.json с v1.12.1 зашифрованы DPAPI и
/// копированием файла не переносятся: экспорт берёт уже расшифрованные
/// настройки из памяти и кладёт их в файл с собственной (парольной)
/// защитой. Машинно-зависимые поля (позиции просмотра, расписание записей,
/// состояние таймеров/разблокировок) не переносятся.
/// </summary>
public class SettingsTransferService
{
    private const int Pbkdf2Iterations = 100_000;

    /// <summary>Поля-одиночки, не имеющие смысла на другой машине.</summary>
    private static void StripMachineSpecific(AppSettings s)
    {
        s.ScheduledRecordings.Clear();
        s.InterruptedRecordings.Clear();
        s.LastUpdateCheckUtc = null;
        s.ParentalControlUnlockedUntilUtc = null;
        s.WindowPlacement = null;
        s.LastWatchedChannel = null;
        s.StatsOverlayVisible = false;
        s.SleepTimerMinutes = 0;
        s.RecordingsFolder = null;
    }

    /// <summary>
    /// Выгружает настройки в файл: JSON-снимок без машинно-зависимых полей,
    /// сжатый GZip и зашифрованный AES-GCM на ключе из пароля. Непрозрачный
    /// формат файла не скрывает его содержимое — защиту обеспечивает только
    /// пароль, о чём диалог предупреждает при экспорте.
    /// </summary>
    public async Task ExportAsync(AppSettings settings, string path, string password)
    {
        // Копия: вычищаем машинно-зависимое, не трогая живые настройки.
        var snapshot = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings))!;
        StripMachineSpecific(snapshot);
        snapshot.ActivePlaylistId = 0; // активность не переносим, выберется при импорте

        var plain = JsonSerializer.SerializeToUtf8Bytes(snapshot);

        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        var envelope = new ExportEnvelope
        {
            Format = "iptvplayer-export",
            Version = 1,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Iterations = Pbkdf2Iterations,
            Data = Convert.ToBase64String(cipher)
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(envelope));
    }

    /// <summary>
    /// Читает файл экспорта. Бросает исключение при неверном пароле или
    /// повреждённом файле — вызывающий код показывает сообщение.
    /// </summary>
    public async Task<AppSettings> ImportAsync(string path, string password)
    {
        var envelope = JsonSerializer.Deserialize<ExportEnvelope>(
            await File.ReadAllTextAsync(path))
            ?? throw new InvalidDataException("Пустой файл экспорта.");

        if (!string.Equals(envelope.Format, "iptvplayer-export", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Это не файл экспорта IptvPlayer.");
        }

        var salt = Convert.FromBase64String(envelope.Salt);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var tag = Convert.FromBase64String(envelope.Tag);
        var cipher = Convert.FromBase64String(envelope.Data);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, envelope.Iterations, HashAlgorithmName.SHA256, 32);

        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("Неверный пароль.");
        }

        return JsonSerializer.Deserialize<AppSettings>(plain)
            ?? throw new InvalidDataException("Файл экспорта повреждён.");
    }

    /// <summary>
    /// Режим импорта: заменить всё (плейлисты, EPG, предпочтения — кроме
    /// машинно-зависимого) или добавить только плейлисты к существующим.
    /// </summary>
    public enum ImportMode
    {
        ReplaceAll,
        PlaylistsOnly
    }

    /// <summary>
    /// Применяет импортированные настройки к текущим: при ReplaceAll живые
    /// настройки становятся копией импорта (машинно-зависимое сохраняется
    /// от текущих), при PlaylistsOnly — плейлисты из файла добавляются с
    /// новыми Id и без активации. Возвращает число добавленных плейлистов
    /// (для ReplaceAll — общее число плейлистов после замены).
    /// </summary>
    public static int Apply(AppSettings current, AppSettings imported, ImportMode mode)
    {
        if (mode == ImportMode.ReplaceAll)
        {
            var machineKept = new
            {
                current.ScheduledRecordings,
                current.InterruptedRecordings,
                current.LastUpdateCheckUtc,
                current.ParentalControlUnlockedUntilUtc,
                current.WindowPlacement,
                current.LastWatchedChannel,
                current.StatsOverlayVisible,
                current.SleepTimerMinutes,
                current.SleepTimerAction,
                current.RecordingsFolder
            };

            // Полная замена через сериализацию: у AppSettings десяток полей,
            // копировать по одному — рассинхрон при любом добавлении.
            var replaced = JsonSerializer.Deserialize<AppSettings>(
                JsonSerializer.Serialize(imported))!;
            replaced.ScheduledRecordings = machineKept.ScheduledRecordings;
            replaced.InterruptedRecordings = machineKept.InterruptedRecordings;
            replaced.LastUpdateCheckUtc = machineKept.LastUpdateCheckUtc;
            replaced.ParentalControlUnlockedUntilUtc = machineKept.ParentalControlUnlockedUntilUtc;
            replaced.WindowPlacement = machineKept.WindowPlacement;
            replaced.LastWatchedChannel = machineKept.LastWatchedChannel;
            replaced.StatsOverlayVisible = machineKept.StatsOverlayVisible;
            replaced.SleepTimerMinutes = machineKept.SleepTimerMinutes;
            replaced.SleepTimerAction = machineKept.SleepTimerAction;
            replaced.RecordingsFolder = machineKept.RecordingsFolder;

            replaced.ActivePlaylistId = replaced.Playlists.FirstOrDefault()?.Id ?? 0;
            CopyAll(replaced, current);
            return current.Playlists.Count;
        }

        // PlaylistsOnly: добавляем с новыми Id, не трогая текущие списки.
        var nextId = current.Playlists.Count == 0
            ? 1
            : current.Playlists.Max(p => p.Id) + 1;
        var added = 0;
        foreach (var playlist in imported.Playlists)
        {
            // Дубликат по URL (и типу для порталов) пропускаем.
            if (current.Playlists.Any(p =>
                    p.Type == playlist.Type &&
                    string.Equals(p.Url, playlist.Url, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            playlist.Id = nextId++;
            playlist.LastWatchedChannel = null;
            current.Playlists.Add(playlist);
            added++;
        }

        if (current.ActivePlaylistId == 0 && current.Playlists.Count > 0)
        {
            current.ActivePlaylistId = current.Playlists[0].Id;
        }

        return added;
    }

    /// <summary>Поверхностное копирование всех полей AppSettings.</summary>
    private static void CopyAll(AppSettings from, AppSettings to)
    {
        var replaced = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(from))!;
        foreach (var property in typeof(AppSettings).GetProperties())
        {
            if (property.CanWrite)
            {
                property.SetValue(to, property.GetValue(replaced));
            }
        }
    }

    private class ExportEnvelope
    {
        public string Format { get; set; } = string.Empty;
        public int Version { get; set; }
        public int Iterations { get; set; }
        public string Salt { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
    }
}
