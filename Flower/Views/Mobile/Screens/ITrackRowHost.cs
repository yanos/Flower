namespace Flower.Views.Mobile.Screens;

// Implemented by whichever screen hosts TrackRowTemplate (TrackListScreenView,
// SearchResultsScreenView) so the shared, class-less template can reach these
// off a typed $parent[ITrackRowHost] ancestor binding instead of an untyped
// $parent[UserControl] one - see TrackRowTemplate.axaml's own comment for why
// each host answers independently rather than the template reading off the
// shared MobileMainViewModel directly.
public interface ITrackRowHost
{
    bool IsAlbumMode { get; }
    bool IsPlaylistMode { get; }
}
