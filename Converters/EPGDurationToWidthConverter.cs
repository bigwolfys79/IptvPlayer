using System;
using Microsoft.UI.Xaml.Data;

namespace IptvPlayer.Converters;

/// <summary>
/// Новый конвертер — раньше ширины программы по длительности не было вообще
/// (EPGTimeToPositionConverter отдавал только позицию). Width = Duration.TotalHours * PixelsPerHour,
/// на том же масштабе, что и EPGTimeToPositionConverter (см. EpgTimelineScale).
/// Bind: Width="{Binding Duration, Converter={StaticResource EPGDurationToWidthConverter}}"
/// </summary>
public partial class EPGDurationToWidthConverter : IValueConverter
{
    private const double MinWidth = 1;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is TimeSpan duration)
        {
            return Math.Max(MinWidth, duration.TotalHours * EpgTimelineScale.PixelsPerHour);
        }

        return MinWidth;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return value;
    }
}
