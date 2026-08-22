using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IptvPlayer.Converters;

/// <summary>
/// Converts a "IsLive" bool into a brush for the channel-list live indicator:
/// green when the channel is live, gray otherwise.
/// </summary>
public partial class BoolToLiveColorConverter : IValueConverter
{
    private static readonly SolidColorBrush LiveBrush = new(Color.FromArgb(255, 76, 175, 80));   // green
    private static readonly SolidColorBrush OfflineBrush = new(Color.FromArgb(255, 158, 158, 158)); // gray

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isLive = value is bool b && b;
        return isLive ? LiveBrush : OfflineBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException("BoolToLiveColorConverter does not support ConvertBack.");
    }
}
