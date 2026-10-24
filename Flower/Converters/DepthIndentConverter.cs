using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Flower.Converters
{
    // A row inside playlist folders, pushed in by how deep it sits
    // (SidebarItem.Depth). The parameter is the row's padding at the top
    // level, as a Thickness string; each folder deeper adds Step to its left.
    public class DepthIndentConverter : IValueConverter
    {
        public static readonly DepthIndentConverter Instance = new();

        private const double Step = 20;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var at = parameter is string text ? Thickness.Parse(text) : default;
            var depth = value is int d ? d : 0;
            return new Thickness(at.Left + depth * Step, at.Top, at.Right, at.Bottom);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
