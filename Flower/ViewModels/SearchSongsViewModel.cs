namespace Flower.ViewModels;

// The desktop Search page's songs, drawn by AlbumTrackList - see
// MainViewModel.SearchSongs. Everything it does is the base class's, but for
// disc headers: its songs are from every album, not one.
public sealed class SearchSongsViewModel : AlbumTracksViewModel
{
    protected override bool ShowsDiscHeaders => false;
}
