using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace IptvPlayer.Converters;

public partial class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        // value can be a genuine bool (e.g. IsPlaying), or an arbitrary
        // reference type used as a "has selection" check (e.g. SelectedChannel).
        // Treat null as false/Collapsed, a bool as itself, and any other
        // non-null object as true/Visible.
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