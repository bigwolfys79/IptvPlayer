using System;
using Microsoft.UI.Xaml.Data;

namespace IptvPlayer.Converters;


public static class EpgTimelineScale
{
    public static DateTime WindowStart { get; set; } = DateTime.Now.AddHours(-72);
    public static double PixelsPerHour { get; set; } = 120;
}


public partial class EPGTimeToPositionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DateTime start)
        {
            var hoursFromWindowStart = (start - EpgTimelineScale.WindowStart).TotalHours;
            return Math.Max(0, hoursFromWindowStart * EpgTimelineScale.PixelsPerHour);
        }

        return 0d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return value;
    }
}
