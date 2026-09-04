using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using IptvPlayer.Models;

namespace IptvPlayer.Services;

/// <summary>
/// Логика родительского контроля: скрытие каналов выбранных групп за PIN
/// с возможностью временной разблокировки (15/30/45/60 мин или до
/// выключения). Чистая логика — покрыта unit-тестами.
///
/// PIN хранится как PBKDF2-SHA256 (соль:хэш, base64), не открытым текстом.
/// Если PIN не установлен, контроль работает как простое скрытие групп без
/// защиты от отключения (осознанный режим «спрятать от гостей»).
/// </summary>
public static class ParentalControlService
{
    // Ключевые слова «взрослых» групп: автопредложение при включении —
    // группы, чьё имя содержит одно из слов (регистронезависимо).
    private static readonly string[] AdultGroupKeywords =
    {
        "18+", "xxx", "adult", "эротик", "для взрослых", "порн", "erotica", "porn", "hustler", "brazzers", "playboy"
    };

    private const int Pbkdf2Iterations = 100_000;

    /// <summary>Похоже ли название группы на «взрослую» (для автоподсказки).</summary>
    public static bool LooksLikeAdultGroup(string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return false;
        }

        var lowered = groupName.ToLowerInvariant();
        return AdultGroupKeywords.Any(k => lowered.Contains(k));
    }

    /// <summary>Заблокированы ли группы прямо сейчас (true = каналы скрыты).</summary>
    public static bool IsLocked(AppSettings settings, DateTime? utcNow = null)
    {
        if (!settings.ParentalControlEnabled)
        {
            return false;
        }

        var now = utcNow ?? DateTime.UtcNow;
        return settings.ParentalControlUnlockedUntilUtc is not { } until || now >= until;
    }

    /// <summary>Группа входит в заблокированный список (сравнение без регистра).</summary>
    public static bool IsGroupBlocked(AppSettings settings, string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return false;
        }

        return settings.ParentalControlBlockedGroups.Contains(groupName.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Временная разблокировка на N минут; null — до выключения.</summary>
    public static void Unlock(AppSettings settings, int? minutes)
    {
        settings.ParentalControlUnlockedUntilUtc = minutes is > 0
            ? DateTime.UtcNow.AddMinutes(minutes.Value)
            : DateTime.MaxValue;
    }

    /// <summary>Снова скрыть группы немедленно.</summary>
    public static void Lock(AppSettings settings)
    {
        settings.ParentalControlUnlockedUntilUtc = null;
    }

    /// <summary>Хэш PIN в виде «соль:хэш» (base64). PIN может быть пустым — вернёт null.</summary>
    public static string? HashPin(string? pin)
    {
        if (string.IsNullOrEmpty(pin))
        {
            return null;
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    /// <summary>Проверка PIN; true, если совпадает (или PIN вообще не установлен).</summary>
    public static bool VerifyPin(AppSettings settings, string? pin)
    {
        if (string.IsNullOrEmpty(settings.ParentalControlPinHash))
        {
            return true; // PIN не установлен — защита от отключения не нужна.
        }

        if (string.IsNullOrEmpty(pin))
        {
            return false;
        }

        var parts = settings.ParentalControlPinHash.Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(pin), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Автопредложение: все «взрослые» группы из списка каналов — их обычно
    /// и хотят скрыть; пользователь может снять/добавить галочки вручную.
    /// </summary>
    public static List<string> SuggestBlockedGroups(IEnumerable<string?> groupNames)
    {
        return groupNames
            .Where(LooksLikeAdultGroup)
            .Select(g => g!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ===================== Дневной лимит просмотра =====================

    /// <summary>Ключ дня для счётчика просмотра (локальная дата).</summary>
    public static string DailyDateKey(DateTime localNow) => localNow.ToString("yyyy-MM-dd");

    /// <summary>
    /// Сбрасывает счётчик просмотра при смене суток. Вызывается перед любым
    /// чтением/увеличением счётчика (значение и дата хранятся в настройках).
    /// </summary>
    public static void ResetWatchedIfNewDay(AppSettings settings, DateTime localNow)
    {
        var today = DailyDateKey(localNow);
        if (!string.Equals(settings.ParentalWatchedDate, today, StringComparison.Ordinal))
        {
            settings.ParentalWatchedDate = today;
            settings.ParentalWatchedSeconds = 0;
        }
    }

    /// <summary>
    /// Исчерпан ли дневной лимит (true = воспроизведение запрещено до
    /// полуночи). Лимит действует только при включённом родительском контроле.
    /// </summary>
    public static bool IsDailyLimitReached(AppSettings settings, DateTime localNow)
    {
        if (!settings.ParentalControlEnabled || settings.ParentalDailyLimitMinutes <= 0)
        {
            return false;
        }

        ResetWatchedIfNewDay(settings, localNow);
        return settings.ParentalWatchedSeconds >= settings.ParentalDailyLimitMinutes * 60L;
    }

    /// <summary>Остаток лимита на сегодня в минутах (0 — исчерпан; неполная
    /// минута округляется вверх). Без лимита — int.MaxValue.</summary>
    public static int GetRemainingMinutes(AppSettings settings, DateTime localNow)
    {
        if (!settings.ParentalControlEnabled || settings.ParentalDailyLimitMinutes <= 0)
        {
            return int.MaxValue;
        }

        ResetWatchedIfNewDay(settings, localNow);
        var remainingSeconds = settings.ParentalDailyLimitMinutes * 60L - settings.ParentalWatchedSeconds;
        if (remainingSeconds <= 0)
        {
            return 0;
        }

        return (int)Math.Min(int.MaxValue, (remainingSeconds + 59) / 60);
    }

    /// <summary>
    /// Добавляет просмотренные секунды к счётчику дня. Смена даты обнуляет
    /// счётчик. На диск настройки пишет вызывающий.
    /// </summary>
    public static void AddWatchedSeconds(AppSettings settings, int seconds, DateTime localNow)
    {
        if (seconds <= 0)
        {
            return;
        }

        ResetWatchedIfNewDay(settings, localNow);
        settings.ParentalWatchedSeconds = (int)Math.Min(
            int.MaxValue, (long)settings.ParentalWatchedSeconds + seconds);
    }

    /// <summary>Сколько осталось до полуночи (сброса лимита) — для сообщения.</summary>
    public static TimeSpan TimeUntilReset(DateTime localNow)
    {
        var midnight = localNow.Date.AddDays(1);
        return midnight - localNow;
    }
}
