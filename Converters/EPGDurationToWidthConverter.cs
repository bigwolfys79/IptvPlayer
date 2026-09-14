using System;
using Microsoft.UI.Xaml.Data;

namespace IptvPlayer.Converters;


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
