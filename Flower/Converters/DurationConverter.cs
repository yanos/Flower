using System;
using System.Globalization;
using Avalonia.Data.Converters;

using Flower.ViewModels;

namespace Flower.Converters
{
    public class DurationConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is TimeSpan ts ? DurationText.Compact(ts) : null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
