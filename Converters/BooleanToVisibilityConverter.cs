using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace IptvPlayer.Converters;

public partial class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {

        bool isVisible = value switch
        {
            null => false,
            bool boolValue => boolValue,
            _ => true
        };

        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return value is Visibility visibility && visibility == Visibility.Visible;
    }
}