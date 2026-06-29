using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace Flower.UserControls
{
    public partial class FilterControl : UserControl
    {
        public static readonly StyledProperty<string?> FilterTextProperty =
            AvaloniaProperty.Register<FilterControl, string?>(
                nameof(FilterText),
                defaultBindingMode: BindingMode.TwoWay);

        public string? FilterText
        {
            get => GetValue(FilterTextProperty);
            set => SetValue(FilterTextProperty, value);
        }

        public FilterControl()
        {
            InitializeComponent();
        }
    }
}
