using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Controls;
using Flower.Models;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Controls;

// The track list's selection rules (TrackRowSelection): plain, Shift-range and
// Primary-toggle presses, the anchor, arrow keys, and the selection carried
// across a rebuild of the rows. MusicListViewGestureTests drives the same
// thing through a real window for the one part only a window has - the
// TwoWay SelectedTrack binding echoing back into the control.
public class TrackRowSelectionTests
{
    // A rebuild (filter/sort/view switch) produces fresh rows over tracks that
    // keep their identity: the same instances, or - after a rescan - new ones
    // carrying the old Track.Id forward (Library.CarryForwardMutableState). The
    // deterministic Id per index is what models that here; without it these
    // rows would be strangers to each other and no selection could survive.
    private static List<TrackRowViewModel> Rows(int count, bool placeholders = false) =>
        Enumerable.Range(0, count).Select(i => new TrackRowViewModel
        {
            Track = new Track
            {
                Id = new Guid(i, 0, 0, new byte[8]),
                // Every undownloaded placeholder shares a null Path, which is
                // exactly why Path cannot be their identity.
                Path = placeholders ? null : $"/music/{i}.mp3",
                Title = $"Track {i}",
                Album = "Album",
                OriginDeviceFingerprint = placeholders ? "server" : null,
            },
        }).ToList();

    private static (TrackRowSelection Selection, List<TrackRowViewModel> Items) Over(List<TrackRowViewModel> items)
    {
        var selection = new TrackRowSelection();
        selection.SetItems(items, null);
        return (selection, items);
    }

    private static (TrackRowSelection Selection, List<TrackRowViewModel> Items) Over(int count) => Over(Rows(count));

    private static void Click(TrackRowSelection s, List<TrackRowViewModel> items, int index) =>
        s.Press(items[index], extend: false, toggle: false);

    private static void ShiftClick(TrackRowSelection s, List<TrackRowViewModel> items, int index) =>
        s.Press(items[index], extend: true, toggle: false);

    private static void PrimaryClick(TrackRowSelection s, List<TrackRowViewModel> items, int index) =>
        s.Press(items[index], extend: false, toggle: true);

    private static int[] Selected(List<TrackRowViewModel> items) =>
        items.Select((r, i) => (r, i)).Where(t => t.r.IsSelected).Select(t => t.i).ToArray();

    [Fact]
    public void A_plain_press_selects_exactly_one_row()
    {
        var (s, items) = Over(10);

        Click(s, items, 3);

        Assert.Equal([3], Selected(items));
        Assert.Same(items[3], s.Current);
    }

    [Fact]
    public void A_plain_press_on_another_row_replaces_the_selection()
    {
        var (s, items) = Over(10);

        Click(s, items, 3);
        Click(s, items, 7);

        Assert.Equal([7], Selected(items));
    }

    [Fact]
    public void Shift_selects_the_inclusive_range_from_the_anchor_either_way()
    {
        var (s, items) = Over(10);

        Click(s, items, 2);
        ShiftClick(s, items, 5);
        Assert.Equal([2, 3, 4, 5], Selected(items));

        Click(s, items, 6);
        ShiftClick(s, items, 4);
        Assert.Equal([4, 5, 6], Selected(items));
    }

    // The anchor is deliberately left where the first plain press put it, so
    // a second Shift+press re-measures from the same start rather than from
    // the previous one's landing row.
    [Fact]
    public void A_second_shift_press_shrinks_the_range_from_the_same_anchor()
    {
        var (s, items) = Over(10);

        Click(s, items, 2);
        ShiftClick(s, items, 8);
        ShiftClick(s, items, 4);

        Assert.Equal([2, 3, 4], Selected(items));
    }

    [Fact]
    public void Primary_adds_a_row_without_clearing_the_rest_and_takes_one_back_out()
    {
        var (s, items) = Over(10);

        Click(s, items, 1);
        PrimaryClick(s, items, 4);
        PrimaryClick(s, items, 8);
        Assert.Equal([1, 4, 8], Selected(items));
        Assert.Equal(3, s.Tracks.Count);

        PrimaryClick(s, items, 8);
        Assert.Equal([1, 4], Selected(items));
        Assert.Same(items[4], s.Current);
    }

    // Deselecting the last remaining row leaves an empty selection rather than
    // silently falling back to the row just pressed.
    [Fact]
    public void Primary_on_the_only_selected_row_leaves_nothing_selected()
    {
        var (s, items) = Over(10);

        Click(s, items, 2);
        PrimaryClick(s, items, 2);

        Assert.Empty(Selected(items));
        Assert.Null(s.Current);
    }

    // A plain press on a row that is already part of a multi-selection keeps
    // the whole selection, so a drag or a context menu acts on all of it
    // rather than on the one row under the pointer.
    [Fact]
    public void A_plain_press_inside_a_multi_selection_keeps_it()
    {
        var (s, items) = Over(10);

        Click(s, items, 2);
        ShiftClick(s, items, 5);
        var changed = s.Press(items[4], extend: false, toggle: false);

        Assert.False(changed);
        Assert.Equal([2, 3, 4, 5], Selected(items));
    }

    [Fact]
    public void Arrow_keys_move_the_selection_and_shift_grows_it_from_the_anchor()
    {
        var (s, items) = Over(5);

        Click(s, items, 1);
        Assert.Equal(2, s.Move(1, extend: false));
        Assert.Equal([2], Selected(items));

        Assert.Equal(3, s.Move(1, extend: true));
        Assert.Equal([2, 3], Selected(items));

        Assert.Equal(4, s.Move(1, extend: false));
        Assert.Equal(-1, s.Move(1, extend: false));
        Assert.Equal([4], Selected(items));
    }

    // Rows are rebuilt from scratch on every SetItems (filter/sort/view switch),
    // so IsSelected never survives on its own - it is re-applied by Track.Id.
    [Fact]
    public void A_multi_selection_survives_a_rebuild()
    {
        var (s, items) = Over(10);
        Click(s, items, 1);
        PrimaryClick(s, items, 4);

        var rebuilt = Rows(10);
        s.SetItems(rebuilt, items[4].Track);

        Assert.Equal([1, 4], Selected(rebuilt));
        Assert.Equal(2, s.Tracks.Count);
        Assert.Same(rebuilt[4], s.Current);
    }

    // A greyed-out (undownloaded, unreachable) row is still an ordinary row
    // for selection and therefore for drag-to-playlist: picking one must pick
    // that one, not every placeholder in the view - and still be that one
    // after a rescan rebuilds the rows mid-gesture.
    [Fact]
    public void Placeholders_are_selected_one_at_a_time_and_stay_themselves_across_a_rebuild()
    {
        var (s, items) = Over(Rows(6, placeholders: true));

        Click(s, items, 2);
        Assert.Equal([2], Selected(items));

        PrimaryClick(s, items, 4);
        s.SetItems(Rows(6, placeholders: true), null);

        Assert.Equal(["Track 2", "Track 4"], s.Tracks.Select(t => t.Title).Order().ToArray());
    }

    // Anything filtered out of the new rows drops out of the selection for
    // good, rather than lingering as a phantom or coming back when the filter
    // does.
    [Fact]
    public void A_rebuild_drops_selected_rows_no_longer_present()
    {
        var (s, items) = Over(10);
        Click(s, items, 1);
        PrimaryClick(s, items, 4);

        s.SetItems(Rows(10).Where((_, i) => i != 4).ToList(), null);
        Assert.Equal("/music/1.mp3", Assert.Single(s.Tracks).Path);

        var restored = Rows(10);
        s.SetItems(restored, null);
        Assert.Equal([1], Selected(restored));
    }
}
