using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace IptvPlayer.Converters;


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

        var image = new BitmapImage(uri);

        if (parameter is string widthText && int.TryParse(widthText, out var decodeWidth) && decodeWidth > 0)
        {
            image.DecodePixelWidth = decodeWidth;
        }

        return image;
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
