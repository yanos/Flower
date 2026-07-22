using Avalonia.Controls;

namespace Flower.Views.Mobile.Screens;

public partial class SearchResultsScreenView : UserControl
{
    // TrackRowTemplate (shared with TrackListScreenView) reads these off
    // whichever UserControl hosts it - see that template's own comment -
    // rather than the shared MobileMainViewModel, so a kept-alive one-back/
    // one-forward instance can show frozen state instead of the live VM's.
    // Search results are never an album header view or a reorderable
    // playlist, so these are always false here - no live/frozen distinction
    // needed, just a stable answer for the template to bind to regardless
    // of which host it's in.
    public bool IsAlbumMode => false;
    public bool IsPlaylistMode => false;

    public SearchResultsScreenView()
    {
        InitializeComponent();
    }
}
