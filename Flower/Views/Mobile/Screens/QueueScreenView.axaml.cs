using Avalonia.Controls;

namespace Flower.Views.Mobile.Screens;

public partial class QueueScreenView : UserControl, ITrackRowHost
{
    // The queue is never one album's header view, and is not reordered here -
    // see TrackRowTemplate for what these switch.
    public bool IsAlbumMode => false;
    public bool IsPlaylistMode => false;

    // A queue is whatever was played from - often an album, but as often the
    // whole library or a playlist of many artists - so the artist stays.
    public bool ShowsRowArtist => true;

    public QueueScreenView()
    {
        InitializeComponent();
    }
}
