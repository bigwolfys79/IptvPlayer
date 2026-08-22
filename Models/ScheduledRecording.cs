using System;

namespace IptvPlayer.Models;

/// <summary>
/// Запланированная запись будущей передачи. Канал хранится ПО ИМЕНИ (StreamUrl
/// содержит токены с ограниченным сроком жизни — на момент записи URL
/// разрешается заново по имени из текущего плейлиста). Запись стартует, пока
/// приложение запущено (никакого фонового сервиса в procesсе нет).
/// </summary>
public class ScheduledRecording
{
    public string ChannelName { get; set; } = string.Empty;

    public string ProgramName { get; set; } = string.Empty;

    public DateTime StartTime { get; set; }

    /// <summary>Длительность передачи в секундах (запись пишется на это время).</summary>
    public double DurationSec { get; set; }
}
