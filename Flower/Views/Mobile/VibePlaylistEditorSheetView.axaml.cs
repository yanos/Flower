using Avalonia.Controls;

using Flower.Views.Mobile.Screens;

namespace Flower.Views.Mobile;

public partial class VibePlaylistEditorSheetView : UserControl, ITrackRowHost
{
    // The preview is the Songs tab's own row (TrackRowTemplate), which asks
    // its host these: a ranking across the library, never one album and
    // never reorderable - the smart playlist editor's answers.
    public bool IsAlbumMode => false;
    public bool IsPlaylistMode => false;
    public bool ShowsRowArtist => true;

    public VibePlaylistEditorSheetView() => InitializeComponent();
}
