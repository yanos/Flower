using System;
using System.Globalization;

using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Flower.Converters
{
    // Solid black when the bound toggle (repeat/shuffle) is enabled, greyed out otherwise.
    public class BoolToActiveColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isActive)
                return isActive ? Brushes.Black : Brushes.Gray;

            return new BindingNotification(new InvalidCastException(), BindingErrorType.Error);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return new BindingNotification(new InvalidOperationException(), BindingErrorType.Error);
        }
    }
}
