using System;
using System.ComponentModel;
using IptvPlayer.Services;
using MemoryPack;

namespace IptvPlayer.Models;


[MemoryPackable]
public partial class EPGEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string EventId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string? ChannelName { get; set; }
    public string ProgramName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? ProgramNumber { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }

    [MemoryPackIgnore]
    public TimeSpan Duration => EndTime - StartTime;

    private bool _isCurrent;


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


    public void RefreshLiveProgress() => OnPropertyChanged(nameof(LiveProgress));

    private bool _hasReminder;


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


    [MemoryPackIgnore]
    public bool CanPlayArchive => StartTime <= DateTime.Now;


    [MemoryPackIgnore]
    public string Title => ProgramName;


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
                return L.T("Vchera") + " " + StartTime.ToString("HH:mm");
            }
            if (date == today.AddDays(1))
            {
                return L.T("Zavtra") + " " + StartTime.ToString("HH:mm");
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
