using System;
using System.Globalization;

using Avalonia.Data.Converters;

namespace Flower.Converters
{
    // A length that may have gone negative, as zero - for sizing something
    // off an offset that runs both ways, like a rubber-band pull's
    // (RubberBandScroll), where only one direction opens up room.
    public class NonNegativeConverter : IValueConverter
    {
        public static readonly NonNegativeConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is double d ? Math.Max(0, d) : 0.0;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
