using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Flower.ViewModels.Mobile;

namespace Flower.Controls;

/// <summary>
/// The "New Playlist" row and the name box it turns into - see the markup for
/// what it looks like and why there is only one of it. The code-behind is the
/// part the ViewModel cannot do: putting the caret in the box when it appears,
/// and deciding which gesture commits the name.
/// </summary>
public partial class NewPlaylistEntry : UserControl
{
    // What the host's own rows are inset by, so this row lines up with them:
    // the picker screen's 16,10 (Button.pickerRow) by default, the sheet's
    // 20,14 (Button.action).
    public static readonly StyledProperty<Thickness> RowPaddingProperty =
        AvaloniaProperty.Register<NewPlaylistEntry, Thickness>(nameof(RowPadding), new Thickness(16, 10));

    public Thickness RowPadding
    {
        get => GetValue(RowPaddingProperty);
        set => SetValue(RowPaddingProperty, value);
    }

    public NewPlaylistEntry()
    {
        InitializeComponent();

        // The row it sits in is what the binding shows and hides - IsVisible is
        // not inherited, so the box's own never changes and watching it would
        // never fire. Focus is posted at Loaded priority rather than taken
        // here: at the moment this runs the row has been told to show but not
        // yet laid out, and a control with no layout refuses focus outright.
        EditorRow.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && EditorRow.IsVisible)
                Dispatcher.UIThread.Post(() => NameBox.Focus(), DispatcherPriority.Loaded);
        };
    }

    private MobileMainViewModel? ViewModel => DataContext as MobileMainViewModel;

    private void NameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            vm.CommitNewPlaylistCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CancelNewPlaylistCommand.Execute(null);
        }
    }

    // Tapping anywhere else is the same answer as Enter: a name that was typed
    // is kept, an empty box creates nothing. On a phone the keyboard's own
    // dismissal is what usually gets here, and there is nowhere to put a
    // confirm button that a thumb would not be covering anyway.
    private void NameBox_LostFocus(object? sender, RoutedEventArgs e) =>
        ViewModel?.CommitNewPlaylistCommand.Execute(null);
}
