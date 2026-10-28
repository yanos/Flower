using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

using Flower.Controls;
using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Controls;

// MusicListPanel is hand-rolled virtualization - uniform-height range math
// plus a grow-only row pool - and was the highest-risk untested surface in the
// codebase (docs/ARCHITECTURE-REVIEW.md §5.5). Every assertion here reads the
// panel's real Children/DataContext after a real measure-arrange pass, not an
// extracted copy of the arithmetic - which rows it realizes, which row a point
// falls on and where a drop lands, never where a row is drawn or how big.
//
// [AvaloniaFact] because the panel builds TrackRowControls, which load XAML and
// need a platform.
[Collection("PlatformDataDirectory")]
public class MusicListPanelTests : PinnedDataDirectory
{
    private const double RowHeight = TrackRowViewModel.RowHeight; // 28
    private readonly ColumnManager _columns = new(new AppSettings(), new AppSettingsStore());

    public MusicListPanelTests() => TestIoc.EnsureConfigured();

    // The art column is hidden unless asked for: with it shown, a run shorter
    // than MinAlbumGroupRows is padded with phantom rows, and the tests that
    // are about virtualization want row index and row position to coincide.
    private MusicListPanel NewPanel(bool art = false)
    {
        _columns.ShowAlbumArt = art;
        return new(_columns);
    }

    // Rows in one album of `groupSize` each, which is what TrackListBuilder
    // produces: the first row of each group is its leader and carries the art
    // that spans down over the rest.
    private static List<TrackRowViewModel> Rows(int count, int groupSize = 1) =>
        Enumerable.Range(0, count).Select(i => new TrackRowViewModel
        {
            Track = new Track { Path = $"/music/{i}.mp3", Title = $"Track {i}", Album = $"Album {i / groupSize}" },
            IsFirstInAlbumGroup = i % groupSize == 0,
            AlbumGroupSize = groupSize,
        }).ToList();

    // The row indices the panel currently has realized and visible.
    private static int[] RenderedIndices(MusicListPanel panel, List<TrackRowViewModel> items) =>
        panel.Children
            .Where(c => c.IsVisible)
            .Select(c => items.IndexOf((TrackRowViewModel)c.DataContext!))
            .OrderBy(i => i)
            .ToArray();

    private static void Layout(MusicListPanel panel, double width = 800, double height = 280)
    {
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
    }

    [AvaloniaFact]
    public void An_empty_list_realizes_no_rows()
    {
        var panel = NewPanel();

        panel.SetItems([]);

        Assert.DoesNotContain(panel.Children, c => c.IsVisible);
    }

    [AvaloniaFact]
    public void Only_the_viewport_plus_a_small_overdraw_is_realized_out_of_a_long_list()
    {
        var items = Rows(1000);
        var panel = NewPanel();
        panel.SetViewport(scrollOffset: 0, viewportHeight: 10 * RowHeight, viewportWidth: 800);

        panel.SetItems(items);

        // Ten rows fit; the panel realizes three extra so a partially-scrolled
        // row at either edge is never blank.
        Assert.Equal(Enumerable.Range(0, 13), RenderedIndices(panel, items));
    }

    [AvaloniaFact]
    public void Scrolling_moves_the_realized_window_to_match_the_offset()
    {
        var items = Rows(1000);
        var panel = NewPanel();
        panel.SetItems(items);

        panel.SetViewport(scrollOffset: 20 * RowHeight, viewportHeight: 10 * RowHeight, viewportWidth: 800);

        Assert.Equal(Enumerable.Range(20, 13), RenderedIndices(panel, items));
    }

    // A row keeps its song while the song stays on screen: scrolling one row
    // along hands one row a new song, where handing them out by position gave
    // every row on screen a new one, every row scrolled past.
    [AvaloniaFact]
    public void Scrolling_one_row_along_hands_a_new_song_to_one_row()
    {
        var items = Rows(1000);
        var panel = NewPanel();
        panel.SetItems(items);
        panel.SetViewport(scrollOffset: 20 * RowHeight, viewportHeight: 10 * RowHeight, viewportWidth: 800);
        var before = panel.Children.Select(c => c.DataContext).ToList();

        panel.SetViewport(scrollOffset: 21 * RowHeight, viewportHeight: 10 * RowHeight, viewportWidth: 800);

        Assert.Equal(Enumerable.Range(21, 13), RenderedIndices(panel, items));
        var handedNew = panel.Children.Where((c, slot) => !ReferenceEquals(c.DataContext, before[slot])).Count();
        Assert.Equal(1, handedNew);
    }

    [AvaloniaFact]
    public void A_partially_scrolled_row_is_still_realized()
    {
        var items = Rows(1000);
        var panel = NewPanel();
        panel.SetItems(items);

        // Two-thirds of the way through row 20 - row 20 is still on screen.
        panel.SetViewport(scrollOffset: 20 * RowHeight + 18, viewportHeight: 10 * RowHeight, viewportWidth: 800);

        Assert.Equal(20, RenderedIndices(panel, items)[0]);
    }

    [AvaloniaFact]
    public void The_realized_window_stops_at_the_end_of_the_list()
    {
        var items = Rows(15);
        var panel = NewPanel();
        panel.SetItems(items);

        panel.SetViewport(scrollOffset: 10 * RowHeight, viewportHeight: 10 * RowHeight, viewportWidth: 800);

        Assert.Equal(Enumerable.Range(10, 5), RenderedIndices(panel, items));
    }

    [AvaloniaFact]
    public void An_album_group_leader_scrolled_off_the_top_is_kept_so_its_art_still_spans_down()
    {
        // One 50-track album: row 0 owns the art for all of it.
        var items = Rows(50, groupSize: 50);
        var panel = NewPanel();
        panel.SetItems(items);

        panel.SetViewport(scrollOffset: 30 * RowHeight, viewportHeight: 5 * RowHeight, viewportWidth: 800);

        var rendered = RenderedIndices(panel, items);
        Assert.Equal(0, rendered[0]);
        Assert.Equal(Enumerable.Range(30, 8), rendered.Skip(1));
    }

    [AvaloniaFact]
    public void A_leader_already_inside_the_viewport_is_not_realized_twice()
    {
        var items = Rows(50, groupSize: 10);
        var panel = NewPanel();
        panel.SetItems(items);

        panel.SetViewport(scrollOffset: 0, viewportHeight: 5 * RowHeight, viewportWidth: 800);

        var visible = panel.Children.Where(c => c.IsVisible).ToList();
        Assert.Equal(visible.Count, visible.Select(c => c.DataContext).Distinct().Count());
    }

    [AvaloniaFact]
    public void Each_visible_group_pulls_in_only_its_own_leader()
    {
        var items = Rows(100, groupSize: 10);
        var panel = NewPanel();
        panel.SetItems(items);

        // Rows 35..40 span the group led by 30 and the group led by 40.
        panel.SetViewport(scrollOffset: 35 * RowHeight, viewportHeight: 3 * RowHeight, viewportWidth: 800);

        var rendered = RenderedIndices(panel, items);
        Assert.Contains(30, rendered);
        Assert.DoesNotContain(20, rendered);
        Assert.DoesNotContain(50, rendered);
    }

    [AvaloniaFact]
    public void The_row_pool_grows_but_never_shrinks_and_extras_are_hidden_rather_than_destroyed()
    {
        var items = Rows(1000);
        var panel = NewPanel();
        panel.SetViewport(scrollOffset: 0, viewportHeight: 40 * RowHeight, viewportWidth: 800);
        panel.SetItems(items);
        var poolAfterTallViewport = panel.Children.Count;

        panel.SetViewport(scrollOffset: 0, viewportHeight: 5 * RowHeight, viewportWidth: 800);

        Assert.Equal(poolAfterTallViewport, panel.Children.Count);
        Assert.Equal(8, panel.Children.Count(c => c.IsVisible));
    }

    [AvaloniaFact]
    public void Replacing_the_items_rebinds_every_row_even_when_the_indices_are_unchanged()
    {
        var panel = NewPanel();
        panel.SetViewport(scrollOffset: 0, viewportHeight: 5 * RowHeight, viewportWidth: 800);
        panel.SetItems(Rows(20));

        // Switching albums while scrolled to the top: same indices, entirely
        // different rows. A pure index comparison would leave the old
        // DataContexts in place.
        var replacement = Rows(20);
        panel.SetItems(replacement);

        Assert.All(
            panel.Children.Where(c => c.IsVisible),
            c => Assert.Contains(c.DataContext, replacement));
    }

    // ── Phantom rows ──────────────────────────────────────────────────────────

    // Consecutive album runs of the given lengths.
    private static List<TrackRowViewModel> Runs(params int[] lengths)
    {
        var rows = new List<TrackRowViewModel>();
        for (int album = 0; album < lengths.Length; album++)
        {
            for (int k = 0; k < lengths[album]; k++)
                rows.Add(new TrackRowViewModel
                {
                    Track = new Track { Path = $"/music/{album}-{k}.mp3", Title = $"Track {k}", Album = $"Album {album}" },
                    IsFirstInAlbumGroup = k == 0,
                    AlbumGroupSize = lengths[album],
                });
        }
        return rows;
    }

    [AvaloniaFact]
    public void A_phantom_slot_hit_tests_as_no_row()
    {
        var panel = NewPanel(art: true);
        panel.SetItems(Runs(1, 2));

        Assert.Equal(0, panel.RowIndexAt(0.5 * RowHeight));
        Assert.Equal(-1, panel.RowIndexAt(1.5 * RowHeight));
        Assert.Equal(-1, panel.RowIndexAt(2.5 * RowHeight));
        Assert.Equal(1, panel.RowIndexAt(3.5 * RowHeight));
        Assert.Equal(-1, panel.RowIndexAt(20 * RowHeight));
    }

    [AvaloniaFact]
    public void A_drop_inside_the_phantom_padding_lands_after_that_album()
    {
        var panel = NewPanel(art: true);
        panel.SetItems(Runs(1, 2));

        Assert.Equal(0, panel.InsertionIndexAt(0.2 * RowHeight));
        Assert.Equal(1, panel.InsertionIndexAt(1.2 * RowHeight));
        Assert.Equal(1, panel.InsertionIndexAt(2.8 * RowHeight));
        Assert.Equal(1, panel.InsertionIndexAt(3 * RowHeight));
        Assert.Equal(2, panel.InsertionIndexAt(4 * RowHeight));
        Assert.Equal(3, panel.InsertionIndexAt(40 * RowHeight));
        // The end of the list is the bottom of its last row, above the padding.
        Assert.Equal(5 * RowHeight, panel.RowTop(3));
    }

    [AvaloniaFact]
    public void A_viewport_opening_on_phantom_slots_keeps_the_album_whose_art_hangs_into_them()
    {
        var items = Runs(1, 1, 1, 1);
        var panel = NewPanel(art: true);
        panel.SetItems(items);

        // Slot 4 is album 1's first phantom; album 1 itself is at slot 3.
        panel.SetViewport(scrollOffset: 4 * RowHeight, viewportHeight: 2 * RowHeight, viewportWidth: 800);

        Assert.Equal(1, RenderedIndices(panel, items)[0]);
    }

    [AvaloniaFact]
    public void Hiding_the_art_column_removes_the_phantom_rows()
    {
        var panel = NewPanel(art: true);
        panel.SetItems(Runs(1, 1, 1));
        // On screen, as it always is in the app: a panel listens to column
        // changes only while attached.
        var window = new Window { Content = panel };
        window.Show();

        _columns.ShowAlbumArt = false;
        Layout(panel);

        Assert.Equal(2, panel.RowIndexAt(2.5 * RowHeight));
        window.Close();
    }

    // ── Selection shape ───────────────────────────────────────────────────────

    // The selection highlight's corners on the row showing `row`.
    private static CornerRadius SelectionCorners(MusicListPanel panel, TrackRowViewModel row) =>
        panel.Children.OfType<TrackRowControl>()
            .Single(c => c.IsVisible && c.DataContext == row)
            .FindControl<Border>("SelectionBorder")!.CornerRadius;

    [AvaloniaFact]
    public void A_run_of_picked_rows_is_rounded_only_at_its_ends()
    {
        var items = Rows(5);
        var panel = NewPanel();
        panel.SetItems(items);

        foreach (var i in new[] { 1, 2, 3 })
            items[i].IsSelected = true;
        panel.RefreshHighlightShape();

        Assert.Equal(new CornerRadius(8, 8, 0, 0), SelectionCorners(panel, items[1]));
        Assert.Equal(new CornerRadius(0), SelectionCorners(panel, items[2]));
        Assert.Equal(new CornerRadius(0, 0, 8, 8), SelectionCorners(panel, items[3]));
    }

    [AvaloniaFact]
    public void A_picked_row_alone_stays_fully_rounded()
    {
        var items = Rows(5);
        var panel = NewPanel();
        panel.SetItems(items);

        items[1].IsSelected = true;
        items[3].IsSelected = true;
        panel.RefreshHighlightShape();

        Assert.Equal(new CornerRadius(8), SelectionCorners(panel, items[1]));
        Assert.Equal(new CornerRadius(8), SelectionCorners(panel, items[3]));
    }

    // Album 0's one song is followed by two phantom slots: the next song is
    // its neighbour in the list but not on screen.
    [AvaloniaFact]
    public void Picked_rows_either_side_of_phantom_padding_do_not_join()
    {
        var items = Runs(1, 3);
        var panel = NewPanel(art: true);
        panel.SetItems(items);

        items[0].IsSelected = true;
        items[1].IsSelected = true;
        panel.RefreshHighlightShape();

        Assert.Equal(new CornerRadius(8), SelectionCorners(panel, items[0]));
        Assert.Equal(new CornerRadius(8), SelectionCorners(panel, items[1]));
    }

    // Playing and pointed at are lit the same as picked: the hover grey on
    // the row touching a run joins it rather than notching it.
    [AvaloniaFact]
    public void The_playing_row_joins_the_picked_run_it_touches()
    {
        var items = Rows(5);
        var panel = NewPanel();
        panel.SetItems(items);

        items[1].IsSelected = true;
        items[2].IsSelected = true;
        items[3].IsCurrentlyPlaying = true;
        panel.RefreshHighlightShape();

        Assert.Equal(new CornerRadius(0), SelectionCorners(panel, items[2]));
        Assert.Equal(new CornerRadius(0, 0, 8, 8), SelectionCorners(panel, items[3]));
    }

    [AvaloniaFact]
    public void The_row_under_the_pointer_joins_the_picked_run_it_touches()
    {
        var items = Rows(5);
        var panel = NewPanel();
        var window = new Window { Content = panel, Width = 800, Height = 400 };
        window.Show();
        panel.SetItems(items);
        items[1].IsSelected = true;
        items[2].IsSelected = true;
        window.UpdateLayout();
        // Wherever the window puts the panel.
        Point RowMiddle(int index) => panel.TranslatePoint(new Point(400, (index + 0.5) * RowHeight), window)!.Value;

        window.MouseMove(RowMiddle(3));

        Assert.Equal(new CornerRadius(8, 8, 0, 0), SelectionCorners(panel, items[1]));
        Assert.Equal(new CornerRadius(0), SelectionCorners(panel, items[2]));
        Assert.Equal(new CornerRadius(0, 0, 8, 8), SelectionCorners(panel, items[3]));

        window.MouseMove(RowMiddle(4));

        Assert.Equal(new CornerRadius(0, 0, 8, 8), SelectionCorners(panel, items[2]));
        Assert.Equal(new CornerRadius(8), SelectionCorners(panel, items[4]));
        window.Close();
    }
}
