using System;
using System.Globalization;

using Avalonia.Data.Converters;

namespace Flower.Converters
{
    // Compares value's string representation to the converter parameter — used for
    // enum-driven selection highlighting (e.g. the mobile tab bar) without needing a
    // dedicated converter per enum.
    public class EqualsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value?.ToString() == parameter?.ToString();

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
