using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Flower.Converters
{
    // A heading set in capitals (the desktop sidebar's LIBRARY, PLAYLISTS,
    // SERVERS) without its text changing anywhere it is read as a name -
    // Avalonia's TextBlock has no text transform of its own.
    public class UpperCaseConverter : IValueConverter
    {
        public static readonly UpperCaseConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            (value as string)?.ToUpper(culture);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
