using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using IptvPlayer.Models;

namespace IptvPlayer.Services;


public static class ParentalControlService
{


    private static readonly string[] AdultGroupKeywords =
    {
        "18+", "xxx", "adult", "эротик", "для взрослых", "порн", "erotica", "porn", "hustler", "brazzers", "playboy"
    };

    private const int Pbkdf2Iterations = 100_000;


    // Check adult group name
    public static bool LooksLikeAdultGroup(string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return false;
        }

        var lowered = groupName.ToLowerInvariant();
        return AdultGroupKeywords.Any(k => lowered.Contains(k));
    }


    // Are groups hidden now
    public static bool IsLocked(AppSettings settings, DateTime? utcNow = null)
    {
        if (!settings.ParentalControlEnabled)
        {
            return false;
        }

        var now = utcNow ?? DateTime.UtcNow;
        return settings.ParentalControlUnlockedUntilUtc is not { } until || now >= until;
    }


    // Is group in blocked list
    public static bool IsGroupBlocked(AppSettings settings, string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return false;
        }

        return settings.ParentalControlBlockedGroups.Contains(groupName.Trim(), StringComparer.OrdinalIgnoreCase);
    }


    // Is PIN required for locked group action
    public static bool IsPinRequiredForGroup(AppSettings settings, string? groupName, DateTime? utcNow = null)
    {
        return IsLocked(settings, utcNow)
            && !string.IsNullOrEmpty(settings.ParentalControlPinHash)
            && IsGroupBlocked(settings, groupName);
    }


    // Temporary unlock for N minutes
    public static void Unlock(AppSettings settings, int? minutes)
    {
        settings.ParentalControlUnlockedUntilUtc = minutes is > 0
            ? DateTime.UtcNow.AddMinutes(minutes.Value)
            : DateTime.MaxValue;
    }


    // Lock groups immediately
    public static void Lock(AppSettings settings)
    {
        settings.ParentalControlUnlockedUntilUtc = null;
        ClearChannelUnlock(settings);
    }


    // Unlock single channel until switch
    public static void UnlockForChannel(AppSettings settings, string channelName, string? group)
    {
        settings.ParentalTempUnlockedChannel = channelName;
        settings.ParentalTempUnlockedGroup = group?.Trim();
    }


    // Clear single channel unlock
    public static void ClearChannelUnlock(AppSettings settings)
    {
        settings.ParentalTempUnlockedChannel = null;
        settings.ParentalTempUnlockedGroup = null;
    }


    // Can channel start now
    public static bool IsChannelAccessible(
        AppSettings settings, string channelName, string? group, DateTime? utcNow = null)
    {
        if (!IsLocked(settings, utcNow) || !IsGroupBlocked(settings, group))
        {
            return true;
        }

        return string.Equals(settings.ParentalTempUnlockedChannel, channelName, StringComparison.Ordinal)
            && string.Equals(settings.ParentalTempUnlockedGroup, group?.Trim(), StringComparison.OrdinalIgnoreCase);
    }


    // Hash PIN with PBKDF2
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


    // Verify PIN against hash
    public static bool VerifyPin(AppSettings settings, string? pin)
    {
        if (string.IsNullOrEmpty(settings.ParentalControlPinHash))
        {
            return true;
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


    public static List<string> SuggestBlockedGroups(IEnumerable<string?> groupNames)
    {
        return groupNames
            .Where(LooksLikeAdultGroup)
            .Select(g => g!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }


    public static string DailyDateKey(DateTime localNow) => localNow.ToString("yyyy-MM-dd");


    public static void ResetWatchedIfNewDay(AppSettings settings, DateTime localNow)
    {
        var today = DailyDateKey(localNow);
        if (!string.Equals(settings.ParentalWatchedDate, today, StringComparison.Ordinal))
        {
            settings.ParentalWatchedDate = today;
            settings.ParentalWatchedSeconds = 0;
        }
    }


    // Is daily watch limit reached
    public static bool IsDailyLimitReached(AppSettings settings, DateTime localNow)
    {
        if (!settings.ParentalControlEnabled || settings.ParentalDailyLimitMinutes <= 0)
        {
            return false;
        }

        ResetWatchedIfNewDay(settings, localNow);
        return settings.ParentalWatchedSeconds >= settings.ParentalDailyLimitMinutes * 60L;
    }


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


    // Accumulate watched seconds
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


    public static TimeSpan TimeUntilReset(DateTime localNow)
    {
        var midnight = localNow.Date.AddDays(1);
        return midnight - localNow;
    }
}
