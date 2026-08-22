using System;
using Microsoft.UI.Xaml.Data;

namespace IptvPlayer.Converters;

/// <summary>
/// Общий контекст масштаба таймлайна EPG. EpgViewModel обновляет WindowStart
/// при навигации (Prev/Next/Today) и держит PixelsPerHour синхронизированным.
///
/// Важно: EPGEntry — простой класс без INotifyPropertyChanged, поэтому смена
/// WindowStart сама по себе не перерисует уже забинженные элементы. EpgViewModel
/// после смены окна пересобирает ObservableCollection'ы EPGEntries (Clear +
/// заново Add), это форсирует переконвертацию всех биндингов, использующих
/// EPGTimeToPositionConverter / EPGDurationToWidthConverter.
/// </summary>
public static class EpgTimelineScale
{
    public static DateTime WindowStart { get; set; } = DateTime.Now.AddHours(-72);
    public static double PixelsPerHour { get; set; } = 120;
}

/// <summary>
/// Раньше конвертер просто возвращал TimeSpan.TotalSeconds без какого-либо
/// масштаба — на 24-часовой шкале в пикселях это давало бессмысленные числа
/// и по факту не работало. Теперь: X = (StartTime - WindowStart) * PixelsPerHour,
/// привязывается к Canvas.Left программы на общей 120-часовой шкале
/// [now-72h .. now+48h].
/// </summary>
public partial class EPGTimeToPositionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DateTime start)
        {
            var hoursFromWindowStart = (start - EpgTimelineScale.WindowStart).TotalHours;
            return Math.Max(0, hoursFromWindowStart * EpgTimelineScale.PixelsPerHour);
        }

        return 0d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return value;
    }
}
