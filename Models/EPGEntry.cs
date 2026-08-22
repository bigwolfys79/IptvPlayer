using System;
using System.ComponentModel;
using IptvPlayer.Services;
using MemoryPack;

namespace IptvPlayer.Models;

/// <summary>
/// Одна передача EPG. [MemoryPackable] — бинарная сериализация для дискового
/// кэша EPG (вместо JSON): 400k+ программ читаются из кэша за миллисекунды
/// вместо секунд. Сериализуются ТОЛЬКО данные; вычисляемые свойства ниже
/// помечены [MemoryPackIgnore] и пересчитываются после загрузки.
/// INotifyPropertyChanged нужен единственному изменяемому в рантайме флагу
/// HasReminder (колокольчик в EPG-списке) — x:Bind Mode=OneWay.
/// </summary>
[MemoryPackable]
public partial class EPGEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string EventId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public string ProgramName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? ProgramNumber { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }

    [MemoryPackIgnore]
    public TimeSpan Duration => EndTime - StartTime;

    private bool _isCurrent;

    /// <summary>
    /// Whether this programme is the one currently airing on its channel.
    /// Set by EpgViewModel when determining the current programme.
    /// INPC-свойство (а не авто-свойство): подсветка карточки и полоса
    /// прогресса текущей передачи в EPG-списке переключаются на лету,
    /// когда минутный таймер переносит IsCurrent на новую передачу —
    /// без уведомления они обновились бы только при пересборке списка.
    /// </summary>
    [MemoryPackIgnore]
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent != value)
            {
                _isCurrent = value;
                OnPropertyChanged(nameof(IsCurrent));
            }
        }
    }

    /// <summary>
    /// Доля прошедшей части передачи (0..1) — тонкая полоса в карточке
    /// текущей передачи EPG-списка. Течёт со временем: уведомление
    /// поднимает RefreshLiveProgress из минутного таймера.
    /// </summary>
    [MemoryPackIgnore]
    public double LiveProgress
    {
        get
        {
            if (!IsCurrent)
            {
                return 0;
            }
            var total = (EndTime - StartTime).TotalSeconds;
            if (total <= 0)
            {
                return 0;
            }
            return Math.Clamp((DateTime.Now - StartTime).TotalSeconds / total, 0.0, 1.0);
        }
    }

    /// <summary>Уведомление для x:Bind LiveProgress (значение зависит от часов).</summary>
    public void RefreshLiveProgress() => OnPropertyChanged(nameof(LiveProgress));

    private bool _hasReminder;

    /// <summary>
    /// На эту передачу поставлено напоминание (колокольчик в списке EPG
    /// подсвечен). Рантайм-состояние, в кэш не пишется — восстанавливается
    /// из настроек при загрузке EPG (ApplyReminderFlags в MainPage).
    /// </summary>
    [MemoryPackIgnore]
    public bool HasReminder
    {
        get => _hasReminder;
        set
        {
            if (_hasReminder != value)
            {
                _hasReminder = value;
                OnPropertyChanged(nameof(HasReminder));
            }
        }
    }

    private bool _hasScheduleRecord;

    /// <summary>
    /// На эту передачу запланирована запись (кнопка записи в EPG подсвечена).
    /// Рантайм-состояние, из настроек (ScheduledRecordings) восстанавливается
    /// вместе с HasReminder.
    /// </summary>
    [MemoryPackIgnore]
    public bool HasScheduleRecord
    {
        get => _hasScheduleRecord;
        set
        {
            if (_hasScheduleRecord != value)
            {
                _hasScheduleRecord = value;
                OnPropertyChanged(nameof(HasScheduleRecord));
            }
        }
    }

    /// <summary>
    /// Можно ли запустить эту передачу в архиве (timeshift) — true только для
    /// передач, которые уже начались. Вычисляется в момент отрисовки элемента
    /// списка; при перезагрузке EPG список пересобирается и значение
    /// пересчитывается. Используется для показа значка "смотреть с начала".
    /// </summary>
    [MemoryPackIgnore]
    public bool CanPlayArchive => StartTime <= DateTime.Now;

    // UI helper properties
    [MemoryPackIgnore]
    public string Title => ProgramName;

    /// <summary>
    /// Время начала для карточки в EPG-списке. Список охватывает окно ±3 дня,
    /// поэтому для передач не сегодня к времени добавляется день: «Вчера»/
    /// «Завтра» для соседних дней и дата (dd.MM) для более дальних — иначе
    /// по одному времени непонятно, за какой день программа. Вычисляется в
    /// момент отрисовки элемента списка.
    /// </summary>
    [MemoryPackIgnore]
    public string StartTimeString
    {
        get
        {
            var today = DateTime.Now.Date;
            var date = StartTime.Date;
            if (date == today)
            {
                return StartTime.ToString("HH:mm");
            }
            if (date == today.AddDays(-1))
            {
                return L.T("Вчера", "Yesterday") + " " + StartTime.ToString("HH:mm");
            }
            if (date == today.AddDays(1))
            {
                return L.T("Завтра", "Tomorrow") + " " + StartTime.ToString("HH:mm");
            }
            return StartTime.ToString("dd.MM, HH:mm");
        }
    }

    [MemoryPackIgnore]
    public TimeSpan StartOffset => StartTime.TimeOfDay;

    [MemoryPackIgnore]
    public string ProgramColor => GetProgramColor();

    [MemoryPackIgnore]
    public string TextColor => GetTextColor();

    private string GetProgramColor()
    {
        return Category?.ToLower() switch
        {
            "news" => "#FF1E90FF",
            "sports" => "#FF32CD32",
            "movie" => "#FF8A2BE2",
            "music" => "#FFFF6347",
            "kids" => "#FFFFD700",
            _ => "#FF4CAF50"
        };
    }

    private string GetTextColor()
    {
        return "#FFFFFFFF";
    }
}
