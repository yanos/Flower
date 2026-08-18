using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

using Flower.Controls;
using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// MusicListPanel is hand-rolled virtualization - uniform-height range math
// plus a grow-only row pool - and was the highest-risk untested surface in the
// codebase (docs/ARCHITECTURE-REVIEW.md §5.5). Every assertion here reads the
// panel's real Children/DataContext/Bounds after a real measure-arrange pass,
// not an extracted copy of the arithmetic.
//
// [AvaloniaFact] because the panel builds TrackRowControls, which load XAML and
// need a platform.
[Collection("PlatformDataDirectory")]
public class MusicListPanelTests : PinnedDataDirectory
{
    private const double RowHeight = TrackRowViewModel.RowHeight; // 28
    private readonly ColumnManager _columns = new(new AppSettings(), new AppSettingsStore());

    public MusicListPanelTests() => TestIoc.EnsureConfigured();

    private MusicListPanel NewPanel() => new(_columns);

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

        Assert.Empty(panel.Children.Where(c => c.IsVisible));
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

    [AvaloniaFact]
    public void The_panel_reports_the_full_list_height_so_the_scrollbar_is_sized_for_every_row()
    {
        var panel = NewPanel();
        panel.SetItems(Rows(1000));

        Layout(panel);

        // Not the realized height - virtualization must be invisible to the
        // ScrollViewer.
        Assert.Equal(1000 * RowHeight, panel.DesiredSize.Height);
    }

    [AvaloniaFact]
    public void The_panel_reports_the_total_column_width_so_a_horizontal_scrollbar_can_appear()
    {
        var panel = NewPanel();
        panel.SetViewport(scrollOffset: 0, viewportHeight: 280, viewportWidth: 100);
        panel.SetItems(Rows(10));

        Layout(panel);

        var expected = TrackRowViewModel.ArtColumnWidth + _columns.VisibleColumns.Sum(c => c.Width);
        Assert.Equal(expected, panel.DesiredSize.Width);
    }

    [AvaloniaFact]
    public void Rows_fill_a_viewport_wider_than_the_columns_need()
    {
        var panel = NewPanel();
        var wide = 5000d;
        panel.SetViewport(scrollOffset: 0, viewportHeight: 280, viewportWidth: wide);
        panel.SetItems(Rows(10));

        Layout(panel, width: wide);

        // Otherwise the selection highlight stops short of the right edge.
        Assert.Equal(wide, panel.DesiredSize.Width);
    }

    [AvaloniaFact]
    public void Every_realized_row_is_arranged_at_its_own_absolute_offset()
    {
        var items = Rows(1000);
        var panel = NewPanel();
        panel.SetItems(items);
        panel.SetViewport(scrollOffset: 20 * RowHeight, viewportHeight: 5 * RowHeight, viewportWidth: 800);

        Layout(panel);

        // Positions are absolute within the scrollable content, not relative to
        // the viewport - the ScrollViewer does the translation.
        foreach (var child in panel.Children.Where(c => c.IsVisible))
        {
            var index = items.IndexOf((TrackRowViewModel)child.DataContext!);
            Assert.Equal(index * RowHeight, child.Bounds.Y);
            Assert.Equal(RowHeight, child.Bounds.Height);
        }
    }

    [AvaloniaFact]
    public void A_hidden_pooled_row_is_not_arranged_over_a_live_one()
    {
        var items = Rows(1000);
        var panel = NewPanel();
        panel.SetViewport(scrollOffset: 0, viewportHeight: 40 * RowHeight, viewportWidth: 800);
        panel.SetItems(items);
        panel.SetViewport(scrollOffset: 0, viewportHeight: 3 * RowHeight, viewportWidth: 800);

        Layout(panel);

        Assert.All(panel.Children.Where(c => !c.IsVisible), c => Assert.Equal(default, c.Bounds));
    }
}
