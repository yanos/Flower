using System.Collections.Generic;
using System.Linq;

using Avalonia;

using Flower.Controls;
using Flower.Models;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Controls;

// What a drag on the track list means (TrackRowDragGesture). UF-15: in a
// playlist, dragging a selection moves all of it, and dragging it off the list
// carries the songs out toward the sidebar's playlists.
public class TrackRowDragGestureTests
{
    private static List<TrackRowViewModel> Rows(int count) =>
        Enumerable.Range(0, count).Select(i => new TrackRowViewModel { Track = new Track { Title = $"Track {i}" } }).ToList();

    private static List<TrackRowViewModel> Selected(List<TrackRowViewModel> rows, params int[] indexes)
    {
        foreach (var i in indexes)
            rows[i].IsSelected = true;
        return rows.Where(r => r.IsSelected).ToList();
    }

    private static readonly Point Start = new(50, 50);

    [Fact]
    public void Dragging_one_of_several_selected_rows_moves_them_all_in_list_order()
    {
        var rows = Rows(5);
        var selected = Selected(rows, 1, 3);
        var drag = new TrackRowDragGesture();

        drag.Press(rows[3], Start, allowReorder: true, selected);

        Assert.Equal(TrackRowDragGesture.Mode.Reorder, drag.Move(new Point(50, 90), overList: true));
        Assert.Equal([rows[1], rows[3]], drag.Rows);
    }

    [Fact]
    public void A_movement_under_the_threshold_is_still_a_click()
    {
        var rows = Rows(3);
        var drag = new TrackRowDragGesture();
        drag.Press(rows[0], Start, allowReorder: true, Selected(rows, 0));

        Assert.Equal(TrackRowDragGesture.Mode.None, drag.Move(new Point(51, 51), overList: true));
    }

    // Off the list toward the sidebar, the songs go out; back over it, it is
    // a reorder again.
    [Fact]
    public void Leaving_the_list_carries_the_songs_out_and_coming_back_reorders()
    {
        var rows = Rows(3);
        var drag = new TrackRowDragGesture();
        drag.Press(rows[0], Start, allowReorder: true, Selected(rows, 0));

        Assert.Equal(TrackRowDragGesture.Mode.Out, drag.Move(new Point(-40, 50), overList: false));
        Assert.Equal(TrackRowDragGesture.Mode.Reorder, drag.Move(new Point(50, 80), overList: true));
    }

    // A list that cannot be reordered (Songs, an album) only ever drags out.
    [Fact]
    public void Where_rows_cannot_be_reordered_a_drag_is_always_out()
    {
        var rows = Rows(3);
        var drag = new TrackRowDragGesture();
        drag.Press(rows[1], Start, allowReorder: false, Selected(rows, 1));

        Assert.Equal(TrackRowDragGesture.Mode.Out, drag.Move(new Point(50, 80), overList: true));
    }

    // The queue's song playing stays where it is, whatever else moves.
    [Fact]
    public void A_row_that_cannot_move_is_left_out_of_the_rows_moved()
    {
        var rows = Rows(4);
        rows[1].CanBeDragged = false;
        var drag = new TrackRowDragGesture();
        drag.Press(rows[2], Start, allowReorder: true, Selected(rows, 1, 2));

        drag.Move(new Point(50, 90), overList: true);

        Assert.Equal([rows[2]], drag.Rows);
    }

    // Dropped among the rows moving, they go before the next one that stays.
    [Fact]
    public void The_place_dropped_at_skips_the_rows_moving()
    {
        var rows = Rows(5);
        var drag = new TrackRowDragGesture();
        drag.Press(rows[1], Start, allowReorder: true, Selected(rows, 1, 2));
        drag.Move(new Point(50, 90), overList: true);

        Assert.Same(rows[3], drag.InsertBefore(rows, 2));
        Assert.Null(drag.InsertBefore(rows, 5));
    }
}
