using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace IptvPlayer.Converters;

/// <summary>
/// Строка → Visibility: непустая строка видима, null/пустая — скрыта.
/// Используется для необязательных элементов интерфейса (описание фильма
/// портала есть не у каждого элемента).
/// </summary>
public partial class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
