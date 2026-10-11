using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

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

        public static readonly StyledProperty<string?> PlaceholderTextProperty =
            AvaloniaProperty.Register<FilterControl, string?>(nameof(PlaceholderText), "Filter…");

        public string? PlaceholderText
        {
            get => GetValue(PlaceholderTextProperty);
            set => SetValue(PlaceholderTextProperty, value);
        }

        public FilterControl()
        {
            InitializeComponent();
        }

        // Cmd/Ctrl+K: the box takes the keyboard with what is in it
        // selected, so typing replaces the filter rather than adding to it.
        public void FocusBox()
        {
            Box.Focus(NavigationMethod.Tab);
            Box.SelectAll();
        }
    }
}
