using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Flower.Models;

namespace Flower.Converters
{
    public class IsCurrentTrackConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count == 2 && values[0] is Track track && values[1] is Track current)
                return ReferenceEquals(track, current);
            return false;
        }
    }
}
