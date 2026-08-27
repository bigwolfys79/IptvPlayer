using System;
using Microsoft.UI.Xaml.Data;

namespace IptvPlayer.Converters;

/// <summary>
/// Секунды (double) → «ч:мм:сс» / «мм:сс» — формат подсказки над бегунком
/// ползунка перемотки VOD (ThumbToolTip показывает сырое значение слайдера).
/// Формат совпадает с PlayerViewModel.FormatArchiveTime.
/// </summary>
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
