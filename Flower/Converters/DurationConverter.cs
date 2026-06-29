using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Flower.Converters
{
    public class DurationConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TimeSpan ts)
                return (int)ts.TotalHours > 0
                    ? ts.ToString(@"h\:mm\:ss")
                    : ts.ToString(@"m\:ss");
            return null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
