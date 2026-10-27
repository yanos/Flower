using System;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;

namespace Flower.Converters
{
    // An artist's initials for the round badge beside their name in the
    // desktop's Artists column: the first letter of each of the first two
    // words ("SNES Drunks" -> "SD"), whole characters rather than UTF-16
    // halves, so a name in any script still gets one.
    public class InitialsConverter : IValueConverter
    {
        public static readonly InitialsConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string name)
                return null;

            var initials = name
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => (Word: word, Start: word.TakeWhile(c => !char.IsLetterOrDigit(c)).Count()))
                .Where(w => w.Start < w.Word.Length)
                .Take(2)
                .Select(w => StringInfo.GetNextTextElement(w.Word, w.Start));
            return string.Concat(initials).ToUpper(culture);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
