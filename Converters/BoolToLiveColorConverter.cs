using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IptvPlayer.Converters;

/// <summary>
/// Преобразует bool IsLive в кисть индикатора эфира в списке каналов:
/// зелёная — канал в эфире, иначе серая.
/// </summary>
public partial class BoolToLiveColorConverter : IValueConverter
{
    private static readonly SolidColorBrush LiveBrush = new(Color.FromArgb(255, 76, 175, 80));
    private static readonly SolidColorBrush OfflineBrush = new(Color.FromArgb(255, 158, 158, 158));

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
