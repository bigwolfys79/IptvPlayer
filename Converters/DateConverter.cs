using System;
using System.Globalization;
using Microsoft.UI.Xaml.Data;

namespace IptvPlayer.Converters;

/// <summary>
/// Converts a DateTime into a short, localized display string
/// (e.g. for showing the currently selected EPG date).
/// </summary>
public partial class DateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DateTime date)
        {
            return string.Empty;
        }

        var culture = string.IsNullOrEmpty(language)
            ? CultureInfo.CurrentCulture
            : new CultureInfo(language);

        if (date.Date == DateTime.Today)
        {
            return Services.L.T("Segodnya");
        }

        if (date.Date == DateTime.Today.AddDays(1))
        {
            return Services.L.T("Zavtra");
        }

        if (date.Date == DateTime.Today.AddDays(-1))
        {
            return Services.L.T("Vchera");
        }

        return date.ToString("d MMMM, dddd", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is string text && DateTime.TryParse(text, out var result))
        {
            return result;
        }

        return DateTime.Today;
    }
}
