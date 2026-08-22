using System;

namespace IptvPlayer.Models;

/// <summary>
/// Напоминание о будущей передаче: тост Windows показывается за
/// AppSettings.ReminderMinutes минут до StartTime (см. таймер в MainPage).
/// Идентификация передачи — ChannelId плейлиста + StartTime (EventId
/// у разных источников разный, а пара «канал+время» уникальна).
/// </summary>
public class ProgramReminder
{
    public int ChannelId { get; set; }

    public string ChannelName { get; set; } = string.Empty;

    public string ProgramName { get; set; } = string.Empty;

    public DateTime StartTime { get; set; }

    /// <summary>Тост по этому напоминанию уже показан (не сохранять повторно).</summary>
    public bool Notified { get; set; }
}
