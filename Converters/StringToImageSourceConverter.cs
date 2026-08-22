using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace IptvPlayer.Converters;

/// <summary>
/// string (URL логотипа) -> ImageSource для x:Bind в DataTemplate. Пустая
/// строка/null -> null: Image с пустым Source просто ничего не рисует,
/// оставляя зарезервированное место в строке канала (строки не "прыгают",
/// когда логотипы догружаются после EPG).
/// </summary>
public partial class StringToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        // BitmapImage по умолчанию кэшируется системой — повторные скроллы
        // списка не перезакачивают одни и те же логотипы.
        return new BitmapImage(uri);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is BitmapImage bmp && bmp.UriSource is not null)
        {
            return bmp.UriSource.ToString();
        }

        return string.Empty;
    }
}
