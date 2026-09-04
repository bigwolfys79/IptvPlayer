using System;
using System.Collections.Generic;
using IptvPlayer.Models;
using IptvPlayer.Services;

namespace IptvPlayer.Tests;

public class ParentalControlServiceTests
{
    private static AppSettings Settings(
        bool enabled = true,
        List<string>? blocked = null,
        DateTime? unlockedUntil = null,
        string? pinHash = null) => new()
    {
        ParentalControlEnabled = enabled,
        ParentalControlBlockedGroups = blocked ?? new List<string>(),
        ParentalControlUnlockedUntilUtc = unlockedUntil,
        ParentalControlPinHash = pinHash
    };

    [Fact]
    public void IsLocked_Disabled_IsNeverLocked()
    {
        var s = Settings(enabled: false, blocked: new List<string> { "18+" });
        Assert.False(ParentalControlService.IsLocked(s, DateTime.UtcNow));
    }

    [Fact]
    public void IsLocked_EnabledAndNoUnlock_IsLocked()
    {
        Assert.True(ParentalControlService.IsLocked(Settings(), DateTime.UtcNow));
    }

    [Fact]
    public void IsLocked_UnlockNotExpired_IsUnlocked()
    {
        var s = Settings(unlockedUntil: DateTime.UtcNow.AddMinutes(10));
        Assert.False(ParentalControlService.IsLocked(s, DateTime.UtcNow));
    }

    [Fact]
    public void IsLocked_UnlockExpired_IsLockedAgain()
    {
        var s = Settings(unlockedUntil: DateTime.UtcNow.AddMinutes(-1));
        Assert.True(ParentalControlService.IsLocked(s, DateTime.UtcNow));
    }

    [Fact]
    public void Unlock_Forever_UsesMaxValue()
    {
        var s = Settings();
        ParentalControlService.Unlock(s, null);
        Assert.Equal(DateTime.MaxValue, s.ParentalControlUnlockedUntilUtc);
        Assert.False(ParentalControlService.IsLocked(s, DateTime.UtcNow.AddYears(1)));
    }

    [Fact]
    public void Unlock_Minutes_SetsDeadline()
    {
        var s = Settings();
        ParentalControlService.Unlock(s, 30);
        Assert.False(ParentalControlService.IsLocked(s, DateTime.UtcNow));
        Assert.True(ParentalControlService.IsLocked(s, DateTime.UtcNow.AddMinutes(31)));
    }

    [Theory]
    [InlineData("18+")]
    [InlineData("XXX")]
    [InlineData("Adult")]
    [InlineData("Эротика")]
    [InlineData("Для взрослых")]
    [InlineData("Порно HD")]
    public void LooksLikeAdultGroup_MatchesKeywords(string group)
    {
        Assert.True(ParentalControlService.LooksLikeAdultGroup(group));
    }

    [Theory]
    [InlineData("Федеральные")]
    [InlineData("Музыка")]
    [InlineData("Спорт")]
    [InlineData(null)]
    [InlineData("")]
    public void LooksLikeAdultGroup_DoesNotMatchNormalGroups(string? group)
    {
        Assert.False(ParentalControlService.LooksLikeAdultGroup(group));
    }

    [Fact]
    public void SuggestBlockedGroups_PicksOnlyAdultOnes()
    {
        var suggested = ParentalControlService.SuggestBlockedGroups(
            new[] { "Федеральные", "18+", "Музыка", "xxx" });
        Assert.Equal(2, suggested.Count);
        Assert.Contains("18+", suggested);
        Assert.Contains("xxx", suggested);
    }

    [Fact]
    public void IsGroupBlocked_CaseAndWhitespaceInsensitive()
    {
        var s = Settings(blocked: new List<string> { "18+" });
        Assert.True(ParentalControlService.IsGroupBlocked(s, "18+"));
        Assert.True(ParentalControlService.IsGroupBlocked(s, " 18+ "));
        Assert.True(ParentalControlService.IsGroupBlocked(s, "18+"));
        Assert.False(ParentalControlService.IsGroupBlocked(s, "Федеральные"));
        Assert.False(ParentalControlService.IsGroupBlocked(s, null));
    }

    [Fact]
    public void Pin_HashAndVerify_Roundtrip()
    {
        var s = Settings();
        s.ParentalControlPinHash = ParentalControlService.HashPin("1234");

        Assert.NotNull(s.ParentalControlPinHash);
        Assert.False(s.ParentalControlPinHash!.Contains("1234", StringComparison.Ordinal));
        Assert.True(ParentalControlService.VerifyPin(s, "1234"));
        Assert.False(ParentalControlService.VerifyPin(s, "1235"));
        Assert.False(ParentalControlService.VerifyPin(s, null));
        Assert.False(ParentalControlService.VerifyPin(s, ""));
    }

    [Fact]
    public void Pin_NotSet_AlwaysVerifies()
    {
        var s = Settings();
        Assert.Null(s.ParentalControlPinHash);
        Assert.True(ParentalControlService.VerifyPin(s, "что угодно"));
    }

    [Fact]
    public void Pin_HashIsSalted_TwoHashesDiffer()
    {
        Assert.NotEqual(
            ParentalControlService.HashPin("1234"),
            ParentalControlService.HashPin("1234"));
    }

    // ===================== Дневной лимит просмотра =====================

    private static readonly DateTime Day = new(2026, 9, 4, 15, 0, 0);

    [Fact]
    public void DailyLimit_DisabledOrZero_NeverReached()
    {
        var noLimit = Settings();
        noLimit.ParentalDailyLimitMinutes = 0;
        Assert.False(ParentalControlService.IsDailyLimitReached(noLimit, Day));

        var limit = Settings(enabled: false);
        limit.ParentalDailyLimitMinutes = 60;
        Assert.False(ParentalControlService.IsDailyLimitReached(limit, Day));
    }

    [Fact]
    public void DailyLimit_AccumulatesWithinLimit()
    {
        var s = Settings();
        s.ParentalDailyLimitMinutes = 60;
        for (var i = 0; i < 60 * 60; i++)
        {
            ParentalControlService.AddWatchedSeconds(s, 1, Day);
        }

        Assert.Equal(60 * 60, s.ParentalWatchedSeconds);
        Assert.True(ParentalControlService.IsDailyLimitReached(s, Day));
        Assert.Equal(0, ParentalControlService.GetRemainingMinutes(s, Day));
    }

    [Fact]
    public void DailyLimit_ResetOnNewDay()
    {
        var s = Settings();
        s.ParentalDailyLimitMinutes = 60;
        ParentalControlService.AddWatchedSeconds(s, 60 * 60, Day);
        Assert.True(ParentalControlService.IsDailyLimitReached(s, Day));

        var nextDay = Day.AddDays(1);
        Assert.False(ParentalControlService.IsDailyLimitReached(s, nextDay));
        Assert.Equal(0, s.ParentalWatchedSeconds);
        Assert.Equal(ParentalControlService.DailyDateKey(nextDay), s.ParentalWatchedDate);
    }

    [Fact]
    public void DailyLimit_ResetDoesNotExtendBeyondLimit()
    {
        // После достижения лимита счётчик не «перескакивает» при чтении.
        var s = Settings();
        s.ParentalDailyLimitMinutes = 30;
        ParentalControlService.AddWatchedSeconds(s, 30 * 60, Day);
        Assert.True(ParentalControlService.IsDailyLimitReached(s, Day));
        Assert.Equal(30 * 60, s.ParentalWatchedSeconds);
    }

    [Fact]
    public void DailyLimit_RemainingWhenUnlimited_IsIntMax()
    {
        var s = Settings();
        Assert.Equal(int.MaxValue, ParentalControlService.GetRemainingMinutes(s, Day));
    }

    [Fact]
    public void DailyLimit_PartialMinute_RoundsRemainingUp()
    {
        var s = Settings();
        s.ParentalDailyLimitMinutes = 60;
        ParentalControlService.AddWatchedSeconds(s, 59, Day);
        Assert.False(ParentalControlService.IsDailyLimitReached(s, Day));
        // Осталась 59 мин 1 с непросмотренных — показываем как 60 мин.
        Assert.Equal(60, ParentalControlService.GetRemainingMinutes(s, Day));
    }

    [Fact]
    public void DailyLimit_TimeUntilReset_ReachesMidnight()
    {
        var evening = new DateTime(2026, 9, 4, 23, 30, 0);
        Assert.Equal(TimeSpan.FromMinutes(30), ParentalControlService.TimeUntilReset(evening));
    }
}
