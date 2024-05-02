using System;

using Avalonia.Data;
using Avalonia.Data.Converters;

using Material.Icons;

namespace Flower.Converters
{
    public class PlayOrPauseConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is bool val)
                return val ? MaterialIconKind.Pause : MaterialIconKind.Play;            

            return new BindingNotification(new InvalidCastException(), BindingErrorType.Error);

        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            return new BindingNotification(new InvalidOperationException(), BindingErrorType.Error);
        }
    }
}
