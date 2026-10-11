using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

using Flower.Controls;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile.Screens;

public partial class QueueScreenView : UserControl, ITrackRowHost, IOpensAtItsOwnPlace
{
    // How much of the last song played shows above the song playing when the
    // screen opens: enough to say there is more up there.
    private const double PlayedPeek = 24;

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
        // Compiled rather than a Binding by name: a name is resolved by
        // reflection at runtime, which a trimmed NativeAOT build can have
        // removed the property behind.
        this.Bind(IsPlaylistModeProperty,
            CompiledBinding.Create((MobileMainViewModel vm) => vm.IsQueueReorderable));

        _ = new TrackRowDragReorder(QueueList, this, DropIndicator, (dragged, insertBefore) =>
        {
            if (DataContext is MobileMainViewModel vm)
                vm.ReorderQueueRow(dragged, insertBefore);
        });
    }

    // Opens on the song playing, just under the header, rather than at the
    // top of what has played. Its row is found rather than worked out from a
    // row height, and twice: the list virtualizes, so the extent the first
    // pass scrolls against is an estimate until the rows around the target
    // are real.
    public void ScrollToItsPlace()
    {
        if (DataContext is not MobileMainViewModel vm)
            return;

        var index = vm.QueuePlayedCount;
        if (index <= 0 || index >= vm.QueueRows.Count)
        {
            Scroller.Offset = default;
            return;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            QueueList.ScrollIntoView(index);
            UpdateLayout();
            if (QueueList.ContainerFromIndex(index) is not { } row
                || row.TranslatePoint(default, QueueContent) is not { } at)
                return;
            Scroller.Offset = new Vector(0, Math.Max(0, at.Y - PlayedPeek));
            UpdateLayout();
        }
    }
}
