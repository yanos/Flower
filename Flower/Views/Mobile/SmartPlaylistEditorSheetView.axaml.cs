using Avalonia.Controls;

using Flower.Views.Mobile.Screens;

namespace Flower.Views.Mobile;

public partial class SmartPlaylistEditorSheetView : UserControl, ITrackRowHost
{
    // The preview under the rules is the Songs tab's own row (TrackRowTemplate),
    // which asks its host these. A preview is a cross-section of the library,
    // never one album and never reorderable - the same answers Search gives.
    public bool IsAlbumMode => false;
    public bool IsPlaylistMode => false;
    public bool ShowsRowArtist => true;

    public SmartPlaylistEditorSheetView() => InitializeComponent();
}
