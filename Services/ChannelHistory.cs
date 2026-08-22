using System;
using System.Collections.Generic;
using IptvPlayer.ViewModels;

namespace IptvPlayer.Services;

/// <summary>
/// Сессионная история просмотра для кнопки «предыдущий канал» (аналог
/// кнопки «назад» пульта). Повторный выбор того же канала подряд не
/// попадает в историю; глубина ограничена, чтобы объекты каналов не
/// удерживались бесконечно. Чистая логика — покрыта unit-тестами.
/// </summary>
public sealed class ChannelHistory
{
    private readonly List<ChannelViewModel> _entries = new();

    public const int MaxEntries = 20;

    public IReadOnlyList<ChannelViewModel> Entries => _entries;

    public bool CanGoBack => _entries.Count > 0;

    /// <summary>Запоминает канал как «предыдущий» (вызывается до смены текущего).</summary>
    public void Record(ChannelViewModel channel)
    {
        if (channel == null)
        {
            return;
        }

        // Тот же канал подряд (перезапуск эфира, выход из архива) — не новое
        // место в истории.
        if (_entries.Count > 0 &&
            ReferenceEquals(_entries[^1], channel))
        {
            return;
        }

        _entries.Add(channel);
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(0);
        }
    }

    /// <summary>Забирает последний канал из истории (или null, если пусто).</summary>
    public ChannelViewModel? Pop()
    {
        if (_entries.Count == 0)
        {
            return null;
        }

        var last = _entries[^1];
        _entries.RemoveAt(_entries.Count - 1);
        return last;
    }

    /// <summary>Очищает историю (например, при переключении плейлиста).</summary>
    public void Clear() => _entries.Clear();
}
