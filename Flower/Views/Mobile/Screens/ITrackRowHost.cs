namespace Flower.Views.Mobile.Screens;

// Implemented by whichever screen hosts TrackRowTemplate (TrackListScreenView,
// SearchResultsScreenView) so the shared row (TrackRowView) can reach these
// off a typed $parent[ITrackRowHost] ancestor binding instead of an untyped
// $parent[UserControl] one - see TrackRowView.axaml's comment on
// IsPlaylistMode/IsAlbumMode for why each host answers independently rather
// than the row reading off the shared MobileMainViewModel directly.
public interface ITrackRowHost
{
    bool IsAlbumMode { get; }
    bool IsPlaylistMode { get; }

    // False when every row on screen carries the same artist, so the rows
    // stop repeating one name down the whole list - an album's own tracks,
    // or a single-artist playlist. The host answers rather than the row: a
    // row only knows its own track, and "the same for this whole view" is a
    // property of the list it is in.
    bool ShowsRowArtist { get; }
}
