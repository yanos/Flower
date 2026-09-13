using System.Collections.Generic;
using System.Linq;

using Avalonia;

using Flower.Controls;

using Xunit;

namespace Flower.Tests;

// docs/ARCHITECTURE-REVIEW.md Tier 4.3: the shift-range / ctrl-toggle /
// drag-threshold gesture that MainView.axaml.cs implemented twice - once for
// SubList, once for the album grids - now lives in one place and is testable
// without a control, a window or a DataContext.
public class NameSelectionDragGestureTests
{
    private readonly List<string> _items = new() { "a", "b", "c", "d", "e" };
    private List<string> _selection = new();

    private NameSelectionDragGesture Create(bool selectOnPlainPress = true) =>
        new(() => _items,
            () => _selection,
            items => _selection = items.ToList(),
            selectOnPlainPress);

    [Fact]
    public void Plain_press_collapses_the_selection_onto_the_pressed_item()
    {
        _selection = new List<string> { "a", "b" };
        var gesture = Create();

        Assert.False(gesture.Press("d", new Point(0, 0), shift: false, toggle: false));

        Assert.Equal(new[] { "d" }, _selection);
    }

    [Fact]
    public void Plain_press_on_an_already_selected_item_preserves_the_whole_selection()
    {
        _selection = new List<string> { "a", "b", "c" };
        var gesture = Create();

        Assert.True(gesture.Press("b", new Point(0, 0), shift: false, toggle: false));

        // Preserved so the batch can be dragged or right-clicked as a unit.
        Assert.Equal(new[] { "a", "b", "c" }, _selection);
    }

    [Fact]
    public void Plain_press_leaves_the_selection_alone_when_selectOnPlainPress_is_off()
    {
        _selection = new List<string> { "a" };
        var gesture = Create(selectOnPlainPress: false);

        gesture.Press("d", new Point(0, 0), shift: false, toggle: false);

        // The album grids' plain click expands a tile in place; it is not a
        // selection change.
        Assert.Equal(new[] { "a" }, _selection);
    }

    [Fact]
    public void Toggle_adds_then_removes_without_touching_the_rest()
    {
        _selection = new List<string> { "a" };
        var gesture = Create();

        gesture.Press("c", new Point(0, 0), shift: false, toggle: true);
        Assert.Equal(new[] { "a", "c" }, _selection);

        gesture.Press("c", new Point(0, 0), shift: false, toggle: true);
        Assert.Equal(new[] { "a" }, _selection);
    }

    [Fact]
    public void Shift_selects_the_range_from_the_last_anchor()
    {
        var gesture = Create();
        gesture.Press("b", new Point(0, 0), shift: false, toggle: false);

        gesture.Press("d", new Point(0, 0), shift: true, toggle: false);

        Assert.Equal(new[] { "b", "c", "d" }, _selection);
    }

    [Fact]
    public void Repeated_shift_clicks_keep_extending_from_the_same_anchor()
    {
        var gesture = Create();
        gesture.Press("c", new Point(0, 0), shift: false, toggle: false);

        gesture.Press("e", new Point(0, 0), shift: true, toggle: false);
        Assert.Equal(new[] { "c", "d", "e" }, _selection);

        // Shrinking back, and then across the anchor in the other direction -
        // both relative to "c", not to the previous click.
        gesture.Press("d", new Point(0, 0), shift: true, toggle: false);
        Assert.Equal(new[] { "c", "d" }, _selection);

        gesture.Press("a", new Point(0, 0), shift: true, toggle: false);
        Assert.Equal(new[] { "a", "b", "c" }, _selection);
    }

    [Fact]
    public void Shift_with_no_anchor_yet_selects_just_the_clicked_item()
    {
        var gesture = Create();

        gesture.Press("c", new Point(0, 0), shift: true, toggle: false);

        Assert.Equal(new[] { "c" }, _selection);
    }

    [Fact]
    public void A_move_within_the_threshold_is_not_a_drag()
    {
        var gesture = Create();
        gesture.Press("a", new Point(100, 100), shift: false, toggle: false);

        Assert.False(gesture.Move(new Point(102, 101)));
        Assert.False(gesture.IsDragging);
        Assert.Null(gesture.DragItems);
    }

    [Fact]
    public void Crossing_the_threshold_starts_a_drag_carrying_the_whole_selection()
    {
        _selection = new List<string> { "a", "b" };
        var gesture = Create();
        gesture.Press("b", new Point(100, 100), shift: false, toggle: false);

        Assert.True(gesture.Move(new Point(100, 110)));

        Assert.True(gesture.IsDragging);
        Assert.Equal(new[] { "a", "b" }, gesture.DragItems!);
    }

    [Fact]
    public void A_drag_started_outside_the_selection_carries_only_the_pressed_item()
    {
        _selection = new List<string> { "a", "b" };
        var gesture = Create(selectOnPlainPress: false);
        gesture.Press("e", new Point(100, 100), shift: false, toggle: false);

        Assert.True(gesture.Move(new Point(100, 110)));

        Assert.Equal(new[] { "e" }, gesture.DragItems!);
        // ...and does not disturb what is selected.
        Assert.Equal(new[] { "a", "b" }, _selection);
    }

    [Fact]
    public void The_drag_set_is_resolved_once_at_threshold_crossing()
    {
        _selection = new List<string> { "a" };
        var gesture = Create();
        gesture.Press("a", new Point(100, 100), shift: false, toggle: false);
        gesture.Move(new Point(100, 110));

        _selection = new List<string> { "a", "b", "c" };
        gesture.Move(new Point(100, 200));

        Assert.Equal(new[] { "a" }, gesture.DragItems!);
    }

    [Fact]
    public void Move_without_a_press_does_nothing()
    {
        var gesture = Create();

        Assert.False(gesture.Move(new Point(500, 500)));
        Assert.False(gesture.IsDragging);
    }

    [Fact]
    public void End_clears_the_drag_but_keeps_the_selection_and_the_anchor()
    {
        var gesture = Create();
        gesture.Press("b", new Point(100, 100), shift: false, toggle: false);
        gesture.Move(new Point(100, 110));

        gesture.End();

        Assert.False(gesture.IsDragging);
        Assert.Null(gesture.DragItems);
        Assert.Null(gesture.PressedItem);
        Assert.Equal(new[] { "b" }, _selection);

        // The anchor survived, so a following Shift+click still ranges from "b".
        gesture.Press("d", new Point(0, 0), shift: true, toggle: false);
        Assert.Equal(new[] { "b", "c", "d" }, _selection);
    }

    [Fact]
    public void Shift_onto_an_item_no_longer_in_the_list_leaves_the_selection_alone()
    {
        _selection = new List<string> { "a" };
        var gesture = Create();
        gesture.Press("a", new Point(0, 0), shift: false, toggle: false);

        gesture.Press("gone", new Point(0, 0), shift: true, toggle: false);

        Assert.Equal(new[] { "a" }, _selection);
    }
}
