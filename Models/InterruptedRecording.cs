using System;

namespace IptvPlayer.Models;

/// <summary>
/// Активная запись, прерванная закрытием приложения. При следующем запуске
/// (если EndTime ещё не прошла) предлагается продолжить запись оставшейся
/// части. Хранится по имени канала: URL потока содержит недолговечные
/// подписи, при продолжении берётся свежий URL из текущего плейлиста.
/// Продолжение пишется в новый файл «… (продолжение)».
/// </summary>
public class InterruptedRecording
{
    public string ChannelName { get; set; } = string.Empty;
    public string ProgramName { get; set; } = string.Empty;

    /// <summary>Когда запись должна закончиться (локальное время);
    /// null — запись без лимита (до ручной остановки).</summary>
    public DateTime? EndTime { get; set; }
}
