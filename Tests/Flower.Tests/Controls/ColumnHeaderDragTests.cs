using System.Linq;

using Flower.Controls;
using Flower.Persistence;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.Controls;

// The track list's column header: a press that stays within the threshold is a
// click and sorts, one that goes past it is a drag and moves the column to the
// gap it is released over (ColumnHeaderDrag). MusicListViewGestureTests drives
// one of each through a real window, for the capture and the deferred reorder.
//
// Pinned because applying a reorder schedules ColumnManager's save.
[Collection("PlatformDataDirectory")]
public class ColumnHeaderDragTests : PinnedDataDirectory
{
    private readonly ColumnManager _columns = new(new AppSettings(), new AppSettingsStore());

    // The middle of the `index`-th visible column's header cell.
    private double CenterOf(int index)
    {
        double cursor = _columns.ArtColumnWidth;
        var visible = _columns.VisibleColumns.ToList();
        for (int i = 0; i < index; i++)
            cursor += visible[i].Width;
        return cursor + visible[index].Width / 2;
    }

    private string[] Order() => _columns.VisibleColumns.Select(c => c.Id).ToArray();

    private void Apply(ColumnHeaderGesture? gesture)
    {
        var reorder = Assert.IsType<ColumnHeaderGesture.Reorder>(gesture);
        _columns.Reorder(reorder.Column, reorder.NewVisibleIndex);
    }

    [Fact]
    public void A_click_sorts_by_the_column()
    {
        var drag = new ColumnHeaderDrag(_columns);
        var column = _columns.VisibleColumns.ElementAt(1);

        drag.Press(column, CenterOf(1));

        Assert.Equal(new ColumnHeaderGesture.Sort(column.Id), drag.Release(CenterOf(1)));
    }

    // Movement below DragThreshold is still a click, not a drag, and shows no
    // drop indicator.
    [Fact]
    public void A_tiny_movement_is_still_a_click()
    {
        var drag = new ColumnHeaderDrag(_columns);
        var column = _columns.VisibleColumns.ElementAt(1);
        double x = CenterOf(1);

        drag.Press(column, x);
        Assert.Null(drag.Move(x + ColumnHeaderDrag.DragThreshold / 2));

        Assert.Equal(new ColumnHeaderGesture.Sort(column.Id), drag.Release(x + ColumnHeaderDrag.DragThreshold / 2));
    }

    // Dragging the first column past the third's midpoint drops it after the
    // third, with the indicator at that gap's edge on the way.
    [Fact]
    public void A_drag_past_the_threshold_moves_the_column_and_does_not_sort()
    {
        var before = Order();
        var drag = new ColumnHeaderDrag(_columns);

        drag.Press(_columns.VisibleColumns.First(), CenterOf(0));
        Assert.NotNull(drag.Move(CenterOf(0) + 20));
        var to = CenterOf(2) + 1;
        var visible = _columns.VisibleColumns.ToList();
        Assert.Equal(_columns.ArtColumnWidth + visible[0].Width + visible[1].Width + visible[2].Width, drag.Move(to));
        Apply(drag.Release(to));

        Assert.Equal([before[1], before[2], before[0]], Order().Take(3).ToArray());
    }

    // A drag that ends where it began is a reorder that changes nothing - and
    // still not a sort.
    [Fact]
    public void A_drag_back_to_its_own_slot_changes_nothing()
    {
        var before = Order();
        var drag = new ColumnHeaderDrag(_columns);
        double x = CenterOf(1);

        drag.Press(_columns.VisibleColumns.ElementAt(1), x);
        drag.Move(x + 30);
        drag.Move(x);
        Apply(drag.Release(x));

        Assert.Equal(before, Order());
    }

    [Fact]
    public void A_drag_past_the_last_column_puts_it_last()
    {
        var before = Order();
        var drag = new ColumnHeaderDrag(_columns);
        double past = _columns.ArtColumnWidth + _columns.VisibleColumns.Sum(c => c.Width) + 50;

        drag.Press(_columns.VisibleColumns.First(), CenterOf(0));
        drag.Move(CenterOf(0) + 20);
        Apply(drag.Release(past));

        Assert.Equal(before[1..].Append(before[0]).ToArray(), Order());
    }

    // A release with no press behind it - the capture was lost first, or the
    // press was not one this gesture takes - asks for nothing.
    [Fact]
    public void A_release_with_no_press_asks_for_nothing()
    {
        var drag = new ColumnHeaderDrag(_columns);
        Assert.Null(drag.Release(CenterOf(1)));

        drag.Press(_columns.VisibleColumns.First(), CenterOf(0));
        drag.End();
        Assert.Null(drag.Move(CenterOf(2)));
        Assert.Null(drag.Release(CenterOf(2)));
    }
}
