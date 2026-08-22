using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace IptvPlayer.Converters;

/// <summary>
/// Обратный BooleanToVisibilityConverter: Visible когда false.
/// Нужен парным элементам: например, в карточке EPG-передачи значок архива
/// показывается у уже начавшихся передач (CanPlayArchive=true), а
/// колокольчик напоминания — у будущих (CanPlayArchive=false).
/// </summary>
public partial class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool b && b ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
