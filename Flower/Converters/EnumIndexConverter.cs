using System;
using System.Globalization;

using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Flower.Converters
{
    // An enum as a ComboBox's SelectedIndex and back, for a picker whose items
    // are listed in the enum's own order - Settings' Theme and Playback
    // Controls, bound straight to MainViewModel.
    public class EnumIndexConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is Enum ? System.Convert.ToInt32(value, culture) : BindingOperations.DoNothing;

        // -1 is the ComboBox with nothing selected, which is no choice at all.
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            return value is int index && index >= 0 && enumType.IsEnum
                ? Enum.ToObject(enumType, index)
                : BindingOperations.DoNothing;
        }
    }
}
