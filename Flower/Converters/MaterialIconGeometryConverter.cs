using System;
using System.Collections.Concurrent;
using System.Globalization;

using Avalonia.Data.Converters;
using Avalonia.Media;

using Material.Icons;

namespace Flower.Converters
{
    // A Material icon's outline, in the 24x24 canvas every icon in the set is
    // drawn on, for a Path inside a 24x24 Canvas. Unlike a MaterialIcon, which
    // scales each glyph to its own bounds, glyphs drawn this way keep their
    // place relative to each other when one replaces another.
    public class MaterialIconGeometryConverter : IValueConverter
    {
        private static readonly ConcurrentDictionary<MaterialIconKind, Geometry> Geometries = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not MaterialIconKind kind)
                return null;

            return Geometries.GetOrAdd(kind, k => StreamGeometry.Parse(MaterialIconDataProvider.GetData(k)));
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
