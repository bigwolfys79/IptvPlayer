using System;
using Microsoft.UI.Xaml.Data;

namespace IptvPlayer.Converters;

public partial class TimeSpanToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is TimeSpan duration)
            return duration.ToString(@"hh\:mm\:ss");
        return "00:00:00";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is string str)
        {
            var parts = str.Split(':');
            if (parts.Length == 3 && int.TryParse(parts[0], out var hours) && int.TryParse(parts[1], out var minutes) && int.TryParse(parts[2], out var seconds))
            {
                return new TimeSpan(hours, minutes, seconds);
            }
        }
        return TimeSpan.Zero;
    }
}