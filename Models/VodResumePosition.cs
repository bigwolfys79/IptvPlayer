using System;

namespace IptvPlayer.Models;

/// <summary>
/// Сохранённая позиция просмотра фильма/серии портала: при повторном
/// открытии предлагается продолжить с этого места. Живёт в AppSettings
/// (ключ — название карточки, для серий — с индексом эпизода).
/// </summary>
public class VodResumePosition
{
    public double PositionSeconds { get; set; }

    /// <summary>Полная длительность на момент сохранения (0 — неизвестна).</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Индекс серии в списке эпизодов (-1 — фильм без серий).</summary>
    public int EpisodeIndex { get; set; } = -1;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Id плейлиста портала, которому принадлежит этот VOD.
    /// Нужен Hub Page для навигации к правильному порталу при resume.
    /// null = неизвестно (fallback на первый portal-плейлист).
    /// </summary>
    public int? PortalPlaylistId { get; set; }
}
