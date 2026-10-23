using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Microsoft.Extensions.Logging;

using Flower.Services;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Views;

// See the markup. A search is remembered as the phone remembers one: when the
// box is left with something in it, Return pressed in it, or a result opened -
// never on each keystroke, or every prefix of a word would be kept as a
// search of its own.
public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
    }

    private MainViewModel? Main => DataContext as MainViewModel;

    // From the container's factory the view model carries, as every view
    // XAML instantiates reaches its logger.
    private ILogger Logger => Main!.LoggerFactory.CreateLogger<SearchView>();

    // Cmd/Ctrl+F and the View menu's Search: the page shown, the box ready to
    // type in. Posted, since the page was hidden a moment ago.
    public void FocusBox() => Dispatcher.UIThread.Post(() => Box.Focus());

    private void Box_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Main?.Search.Remember();
    }

    private void Box_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (Main?.Search is { HasQuery: true } search)
            search.Remember();
    }

    private void Album_Click(object? sender, RoutedEventArgs e)
    {
        if (Main is not { } vm || (sender as Control)?.DataContext is not AlbumTileViewModel tile)
            return;
        vm.Search.Remember();
        vm.ShowAlbumAsync(tile).Forget(Logger, "Opening an album from Search");
    }

    private void Artist_Click(object? sender, RoutedEventArgs e)
    {
        if (Main is not { } vm || (sender as Control)?.DataContext is not ArtistPickerRow artist)
            return;
        vm.Search.Remember();
        vm.ShowArtistAsync(artist.Name).Forget(Logger, "Opening an artist from Search");
    }
}
