namespace Flower.ViewModels;

// The desktop Search page's songs, drawn by AlbumTrackList - see
// MainViewModel.SearchSongs. Everything it does is the base class's, but for
// disc headers and covers: its songs are from every album, not one, so each
// row carries its own cover and no disc headers.
public sealed class SearchSongsViewModel : AlbumTracksViewModel
{
    protected override bool ShowsDiscHeaders => false;
    protected override bool ShowsArt => true;
}
