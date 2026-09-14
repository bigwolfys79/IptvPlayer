using System;
using Microsoft.UI.Xaml.Data;

namespace IptvPlayer.Converters;


public partial class SecondsToTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        double seconds = value switch
        {
            double d => d,
            int i => i,
            long l => l,
            _ => 0
        };

        return ViewModels.PlayerViewModel.FormatArchiveTime(seconds);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return 0.0;
    }
}
