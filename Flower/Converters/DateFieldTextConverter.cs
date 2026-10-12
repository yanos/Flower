using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;

namespace Flower.Converters
{
    // A date as one line of text, for a date field that reads like the combo
    // boxes beside it: the culture's long date without its weekday - "10
    // octobre 2026", "October 10, 2026", "2026年10月10日". The weekday is noise
    // in a rule ("is before Saturday, October 10, 2026") and the widest part of
    // most cultures' pattern, which a phone's row has no room for.
    public partial class DateFieldTextConverter : IValueConverter
    {
        public static readonly DateFieldTextConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is DateTimeOffset date ? date.ToString(PatternFor(culture), culture) : null;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();

        internal static string PatternFor(CultureInfo culture)
            => Weekday().Replace(culture.DateTimeFormat.LongDatePattern, " ").Trim();

        // "dddd" with the commas and spaces that join it to the rest, wherever
        // the culture puts it: first ("dddd, MMMM d, yyyy"), last ("yyyy년 M월
        // d일 dddd") or between.
        [GeneratedRegex(@"[\s,]*dddd[\s,]*")]
        private static partial Regex Weekday();
    }
}
