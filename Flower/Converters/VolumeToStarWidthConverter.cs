using System;
using System.Globalization;

using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Flower.Converters
{
    // Converts a 0-100 Volume value into a GridLength star-weight, so a
    // two-column Grid can render a proportional filled/remaining volume rail
    // without needing to know the row's actual pixel width at bind time.
    // Pass ConverterParameter="Remaining" for the second column (100 -
    // Volume); omit it (or pass anything else) for the filled column
    // (Volume itself).
    public class VolumeToStarWidthConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not int volume)
                return new GridLength(1, GridUnitType.Star);

            var weight = string.Equals(parameter as string, "Remaining", StringComparison.OrdinalIgnoreCase)
                ? 100 - volume
                : volume;

            return new GridLength(Math.Max(weight, 0.0001), GridUnitType.Star);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
