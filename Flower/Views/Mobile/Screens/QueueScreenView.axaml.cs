using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile.Screens;

public partial class QueueScreenView : UserControl, ITrackRowHost
{
    public static readonly StyledProperty<bool> IsPlaylistModeProperty =
        AvaloniaProperty.Register<QueueScreenView, bool>(nameof(IsPlaylistMode));

    // The queue is never one album's header view.
    public bool IsAlbumMode => false;

    // Reorderable like a playlist, except under shuffle - see
    // MobileMainViewModel.IsQueueReorderable. Bound rather than constant,
    // since shuffle is turned on and off with this screen showing.
    public bool IsPlaylistMode
    {
        get => GetValue(IsPlaylistModeProperty);
        private set => SetValue(IsPlaylistModeProperty, value);
    }

    // A queue is whatever was played from - often an album, but as often the
    // whole library or a playlist of many artists - so the artist stays.
    public bool ShowsRowArtist => true;

    public QueueScreenView()
    {
        InitializeComponent();
        this.Bind(IsPlaylistModeProperty, new Binding(nameof(MobileMainViewModel.IsQueueReorderable)));

        _ = new TrackRowDragReorder(QueueList, this, DropIndicator, (dragged, insertBefore) =>
        {
            if (DataContext is MobileMainViewModel vm)
                vm.ReorderQueueRow(dragged, insertBefore);
        });
    }
}
