using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IptvPlayer.Converters;

/// <summary>
/// bool → цвет точки записи в карточке EPG: передача запланирована к записи —
/// красный (классический REC), нет — приглушённо-серый.
/// </summary>
public partial class BoolToRecordColorConverter : IValueConverter
{
    private static readonly Brush Record = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0x3B, 0x3B));
    private static readonly Brush Idle = new SolidColorBrush(Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool b && b ? Record : Idle;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
