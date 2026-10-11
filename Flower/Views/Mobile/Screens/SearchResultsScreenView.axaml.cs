using System.Windows.Input;

using Avalonia.Controls;

using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile.Screens;

public partial class SearchResultsScreenView : UserControl, ITrackRowHost, IArtistRowHost
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

    // Search results are a cross-section of the library rather than one
    // album's worth of tracks, so the artist is what tells two same-titled
    // hits apart - always worth a line here.
    public bool ShowsRowArtist => true;

    public SearchResultsScreenView()
    {
        InitializeComponent();
    }

    public ICommand? OpenArtistCommand => (DataContext as MobileMainViewModel)?.SelectSearchArtistCommand;
}
