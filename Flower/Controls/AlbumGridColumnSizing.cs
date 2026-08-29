using Avalonia;
using Avalonia.Controls;

using Flower.ViewModels.Mobile;

namespace Flower.Controls;

// Feeds the mobile album grids' column count off their own measured width, so
// the tiles keep a sane size instead of ballooning when the phone is turned
// sideways (or the app runs on a tablet) - two 150px-ish tiles in portrait,
// five across in landscape. Attached to the ItemsControl inside each of the
// three art grids (AlbumGridScreenView and friends); AlbumGridRow.ColumnsFor
// owns the width-to-columns arithmetic and MobileMainViewModel.AlbumGridColumns
// the re-chunking that follows from it.
//
// The measured control is the ItemsControl rather than the window, so its own
// margin - and anything else eating into the width, safe-area insets on a
// notched phone in landscape especially - is already subtracted by the time
// this sees it.
//
// All three grids write the one shared property. They are mutually exclusive
// screens of equal width so they always agree; a grid that isn't laid out
// reports 0, which ColumnsFor floors to the two-column default rather than
// letting it clobber a good value.
public static class AlbumGridColumnSizing
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(AlbumGridColumnSizing));

    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);

    static AlbumGridColumnSizing()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.GetNewValue<bool>())
            {
                control.PropertyChanged += OnControlPropertyChanged;
                Apply(control);
            }
            else
            {
                control.PropertyChanged -= OnControlPropertyChanged;
            }
        });
    }

    private static void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty && sender is Control control)
            Apply(control);
    }

    private static void Apply(Control control)
    {
        if (control.DataContext is MobileMainViewModel vm && control.Bounds.Width > 0)
            vm.AlbumGridColumns = AlbumGridRow.ColumnsFor(control.Bounds.Width);
    }
}
