using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Controls;
using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// The half of docs/ARCHITECTURE-REVIEW.md §5.5 that MusicListPanelTests left
// open: MusicListView's own pointer gestures - shift-range / ctrl-toggle
// selection, and column-header click-vs-drag.
//
// These drive the real gesture end to end through a shown headless window's
// input pipeline (HeadlessWindowExtensions.MouseDown/MouseMove/MouseUp), not
// by calling the private handlers - which is the only way the click-vs-drag
// threshold, the pointer capture that carries a drag past the cell it started
// on, and the TwoWay SelectedTrack echo guard are exercised at all.
//
// Geometry is deterministic and comes straight from the control's own layout:
// MusicListView.axaml is a Grid with RowDefinitions="28,*", so the header
// occupies y in [0,28) and row `i` of the (unscrolled) list occupies
// y in [28 + i*28, 28 + (i+1)*28).
[Collection("PlatformDataDirectory")]
public class MusicListViewGestureTests : PinnedDataDirectory
{
    private const double RowHeight  = TrackRowViewModel.RowHeight; // 28
    private const double HeaderHeight = 28;

    public MusicListViewGestureTests()
    {
        TestIoc.EnsureConfigured();
        // MusicListView service-locates the one shared ColumnManager (§2.3),
        // so column order survives between tests in this class - reset it.
        ResetColumnOrder();
    }

    private static ColumnManager Columns => Ioc.Default.GetService<ColumnManager>()!;

    private static void ResetColumnOrder()
    {
        for (int i = 0; i < Columns.Columns.Count; i++)
            Columns.Columns[i].Order = i;
    }

    // Ctrl on Windows/Linux, Meta on macOS - PlatformShortcuts.Primary is what
    // the control itself checks, so the test has to press the same one.
    private static RawInputModifiers PrimaryModifier =>
        PlatformShortcuts.Primary == KeyModifiers.Meta ? RawInputModifiers.Meta : RawInputModifiers.Control;

    // A rebuild (filter/sort/view switch) produces fresh rows over tracks that
    // keep their identity: the same instances, or - after a rescan - new ones
    // carrying the old Track.Id forward (Library.CarryForwardMutableState). The
    // deterministic Id per index is what models that here; without it these
    // rows would be strangers to each other and no selection could survive.
    private static List<TrackRowViewModel> Rows(int count) =>
        Enumerable.Range(0, count).Select(i => new TrackRowViewModel
        {
            Track = new Track
            {
                Id = TrackId(i),
                Path = $"/music/{i}.mp3",
                Title = $"Track {i}",
                Album = "Album",
            },
        }).ToList();

    private static Guid TrackId(int index) => new(index, 0, 0, new byte[8]);

    // The same list, but every track is an undownloaded placeholder - the
    // greyed-out rows an unreachable server leaves behind. They all share a
    // null Path, which is exactly why Path cannot be their identity.
    private static List<TrackRowViewModel> PlaceholderRows(int count) =>
        Enumerable.Range(0, count).Select(i => new TrackRowViewModel
        {
            Track = new Track
            {
                Id = TrackId(i),
                Path = null,
                Title = $"Track {i}",
                Album = "Album",
                OriginDeviceFingerprint = "server",
            },
        }).ToList();

    private sealed class Harness : IDisposable
    {
        public Window Window { get; }
        public MusicListView View { get; }
        public List<TrackRowViewModel> Items { get; }

        public Harness(List<TrackRowViewModel> rows) : this(rows, null) { }

        public Harness(int rowCount) : this(null, rowCount) { }

        private Harness(List<TrackRowViewModel>? rows, int? rowCount)
        {
            View   = new MusicListView();
            Items  = rows ?? Rows(rowCount!.Value);
            Window = new Window { Width = 1400, Height = 600, Content = View };
            Window.Show();
            View.SetItems(Items);
            Dispatcher.UIThread.RunJobs();
            View.UpdateLayout();
        }

        // Center of row `index` in window coordinates.
        public Point Row(int index) => new(200, HeaderHeight + index * RowHeight + RowHeight / 2);

        public void Click(int index, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            var p = Row(index);
            Window.MouseDown(p, MouseButton.Left, modifiers);
            Window.MouseUp(p, MouseButton.Left, modifiers);
            Dispatcher.UIThread.RunJobs();
        }

        public int[] SelectedIndices() =>
            Items.Select((r, i) => (r, i)).Where(t => t.r.IsSelected).Select(t => t.i).ToArray();

        public void Dispose() => Window.Close();
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void PlainClickSelectsExactlyOneRow()
    {
        using var h = new Harness(10);

        h.Click(3);

        Assert.Equal(new[] { 3 }, h.SelectedIndices());
        Assert.Equal(h.Items[3].Track, h.View.SelectedTrack);
    }

    [AvaloniaFact]
    public void PlainClickOnAnotherRowReplacesTheSelection()
    {
        using var h = new Harness(10);

        h.Click(3);
        h.Click(7);

        Assert.Equal(new[] { 7 }, h.SelectedIndices());
    }

    [AvaloniaFact]
    public void ShiftClickSelectsTheInclusiveRangeFromTheAnchor()
    {
        using var h = new Harness(10);

        h.Click(2);
        h.Click(5, RawInputModifiers.Shift);

        Assert.Equal(new[] { 2, 3, 4, 5 }, h.SelectedIndices());
    }

    [AvaloniaFact]
    public void ShiftClickSelectsUpwardsToo()
    {
        using var h = new Harness(10);

        h.Click(6);
        h.Click(4, RawInputModifiers.Shift);

        Assert.Equal(new[] { 4, 5, 6 }, h.SelectedIndices());
    }

    // The anchor is deliberately left where the first plain click put it, so a
    // second shift-click re-measures from the same start rather than from the
    // previous shift-click's landing row.
    [AvaloniaFact]
    public void SecondShiftClickShrinksTheRangeFromTheSameAnchor()
    {
        using var h = new Harness(10);

        h.Click(2);
        h.Click(8, RawInputModifiers.Shift);
        h.Click(4, RawInputModifiers.Shift);

        Assert.Equal(new[] { 2, 3, 4 }, h.SelectedIndices());
    }

    [AvaloniaFact]
    public void PrimaryClickAddsARowWithoutClearingTheRest()
    {
        using var h = new Harness(10);

        h.Click(1);
        h.Click(4, PrimaryModifier);
        h.Click(8, PrimaryModifier);

        Assert.Equal(new[] { 1, 4, 8 }, h.SelectedIndices());
        Assert.Equal(3, h.View.SelectedTracks.Count);
    }

    [AvaloniaFact]
    public void PrimaryClickOnASelectedRowDeselectsIt()
    {
        using var h = new Harness(10);

        h.Click(1);
        h.Click(4, PrimaryModifier);
        h.Click(1, PrimaryModifier);

        Assert.Equal(new[] { 4 }, h.SelectedIndices());
        Assert.Equal(h.Items[4].Track, h.View.SelectedTrack);
    }

    // Deselecting the last remaining row leaves an empty selection rather than
    // silently falling back to the row just clicked.
    [AvaloniaFact]
    public void PrimaryClickOnTheOnlySelectedRowLeavesNothingSelected()
    {
        using var h = new Harness(10);

        h.Click(2);
        h.Click(2, PrimaryModifier);

        Assert.Empty(h.SelectedIndices());
        Assert.Null(h.View.SelectedTrack);
    }

    // A plain press on a row that is already part of a multi-selection must
    // preserve the whole selection, so a drag or a context menu acts on all of
    // it rather than on the one row under the pointer.
    [AvaloniaFact]
    public void PlainPressInsideAMultiSelectionKeepsIt()
    {
        using var h = new Harness(10);

        h.Click(2);
        h.Click(5, RawInputModifiers.Shift);
        h.Click(4);

        Assert.Equal(new[] { 2, 3, 4, 5 }, h.SelectedIndices());
    }

    [AvaloniaFact]
    public void ClickBelowTheLastRowLeavesTheSelectionAlone()
    {
        using var h = new Harness(4);

        h.Click(1);
        h.Window.MouseDown(new Point(200, HeaderHeight + 20 * RowHeight), MouseButton.Left);
        h.Window.MouseUp(new Point(200, HeaderHeight + 20 * RowHeight), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { 1 }, h.SelectedIndices());
    }

    // Rows are rebuilt from scratch on every SetItems (filter/sort/view switch),
    // so IsSelected never survives on its own - it is re-applied by Track.Id.
    [AvaloniaFact]
    public void MultiSelectionSurvivesSetItemsById()
    {
        using var h = new Harness(10);

        h.Click(1);
        h.Click(4, PrimaryModifier);

        var rebuilt = Rows(10);
        h.View.SetItems(rebuilt);

        Assert.Equal(new[] { 1, 4 }, rebuilt.Select((r, i) => (r, i))
                                            .Where(t => t.r.IsSelected).Select(t => t.i).ToArray());
        Assert.Equal(2, h.View.SelectedTracks.Count);
    }

    // A greyed-out (undownloaded, unreachable) row is still an ordinary row for
    // selection and therefore for drag-to-playlist: picking one must pick that
    // one, not every placeholder in the view. They all have a null Path, so
    // when selection was keyed by path they were one and the same key.
    [AvaloniaFact]
    public void SelectingOnePlaceholderDoesNotSelectEveryPlaceholder()
    {
        using var h = new Harness(PlaceholderRows(6));

        h.Click(2);

        Assert.Equal(new[] { 2 }, h.SelectedIndices());
        Assert.Equal("Track 2", Assert.Single(h.View.SelectedTracks).Title);
    }

    // The drag-to-playlist path proper: SelectedTracks is read after a rebuild
    // (a rescan finishing mid-gesture rebuilds the rows underneath), and it
    // must still be the one track that was picked.
    [AvaloniaFact]
    public void APlaceholderSelectionSurvivesARebuildAsItself()
    {
        using var h = new Harness(PlaceholderRows(6));

        h.Click(2);
        h.Click(4, PrimaryModifier);
        h.View.SetItems(PlaceholderRows(6));

        Assert.Equal(
            new[] { "Track 2", "Track 4" },
            h.View.SelectedTracks.Select(t => t.Title).OrderBy(t => t).ToArray());
    }

    // Anything filtered out of the new item set drops out of the selection
    // rather than lingering in SelectedTracks as a phantom.
    [AvaloniaFact]
    public void SetItemsDropsSelectedRowsNoLongerPresent()
    {
        using var h = new Harness(10);

        h.Click(1);
        h.Click(4, PrimaryModifier);

        var filtered = Rows(10).Where((_, i) => i != 4).ToList();
        h.View.SetItems(filtered);

        Assert.Single(h.View.SelectedTracks);
        Assert.Equal("/music/1.mp3", h.View.SelectedTracks[0].Path);

        // And it is dropped for good: switching back to the unfiltered view
        // must not resurrect it. Only the retained id is re-applied.
        var restored = Rows(10);
        h.View.SetItems(restored);

        Assert.Equal(new[] { 1 }, restored.Select((r, i) => (r, i))
                                          .Where(t => t.r.IsSelected).Select(t => t.i).ToArray());
    }

    // The whole gesture stack driven through a real TwoWay binding to a source
    // that re-raises on every write - the arrangement MainView.axaml actually
    // uses (MusicListView.SelectedTrack <-> MainViewModel.SelectedTrack, which
    // forwards to PlaylistControlViewModel and raises PropertyChanged, writing
    // straight back into the control within the same call).
    [AvaloniaFact]
    public void MultiSelectionSurvivesTheRealTwoWayBindingRoundTrip()
    {
        using var h = new Harness(10);
        var source = new EchoingSelectedTrackSource();
        h.View.Bind(MusicListView.SelectedTrackProperty,
                    new Avalonia.Data.Binding(nameof(EchoingSelectedTrackSource.SelectedTrack))
                    {
                        Source = source,
                        Mode   = Avalonia.Data.BindingMode.TwoWay,
                    });

        h.Click(2);
        h.Click(5, RawInputModifiers.Shift);

        Assert.Equal(new[] { 2, 3, 4, 5 }, h.SelectedIndices());
        Assert.Equal(h.Items[5].Track, source.SelectedTrack);
    }

    private sealed class EchoingSelectedTrackSource : System.ComponentModel.INotifyPropertyChanged
    {
        private Track? _selectedTrack;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public Track? SelectedTrack
        {
            get => _selectedTrack;
            set
            {
                // Unconditional raise, exactly like PlaylistControlViewModel's
                // setter - this is what produces the write-back echo.
                _selectedTrack = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectedTrack)));
            }
        }
    }

    // The TwoWay SelectedTrack binding writes our own change straight back into
    // the control within the same call; unguarded, that echo collapses a
    // multi-selection to one row the instant it is built.
    [AvaloniaFact]
    public void EchoingSelectedTrackBackDoesNotCollapseAMultiSelection()
    {
        using var h = new Harness(10);

        h.Click(2);
        h.Click(5, RawInputModifiers.Shift);

        h.View.SetValue(MusicListView.SelectedTrackProperty, h.View.SelectedTrack);

        Assert.Equal(new[] { 2, 3, 4, 5 }, h.SelectedIndices());
    }

    // A genuine external "select this track" request - a different track than
    // the one we last raised - still collapses to that single row.
    [AvaloniaFact]
    public void ExternallySettingSelectedTrackSelectsThatRowAlone()
    {
        using var h = new Harness(10);

        h.Click(2);
        h.Click(5, RawInputModifiers.Shift);

        h.View.SetValue(MusicListView.SelectedTrackProperty, h.Items[8].Track);

        Assert.Equal(new[] { 8 }, h.SelectedIndices());
    }

    // ── Column header click vs drag ───────────────────────────────────────────

    // x-coordinate of the center of the `index`-th visible column's header cell.
    private static double HeaderCenterX(int index)
    {
        double cursor = TrackRowViewModel.ArtColumnWidth;
        var cols = Columns.VisibleColumns.ToList();
        for (int i = 0; i < index; i++)
            cursor += cols[i].Width;
        return cursor + cols[index].Width / 2;
    }

    private static Point HeaderPoint(double x) => new(x, HeaderHeight / 2);

    [AvaloniaFact]
    public void ClickingAHeaderRequestsASortAndDoesNotReorder()
    {
        using var h = new Harness(5);
        var before = Columns.VisibleColumns.Select(c => c.Id).ToList();
        string? sorted = null;
        h.View.SortRequested += (_, id) => sorted = id;

        var p = HeaderPoint(HeaderCenterX(1));
        h.Window.MouseDown(p, MouseButton.Left);
        h.Window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before[1], sorted);
        Assert.Equal(before, Columns.VisibleColumns.Select(c => c.Id).ToList());
    }

    // Movement below DragThreshold (4px) is still a click, not a drag.
    [AvaloniaFact]
    public void TinyMovementOnAHeaderStillSorts()
    {
        using var h = new Harness(5);
        var before = Columns.VisibleColumns.Select(c => c.Id).ToList();
        string? sorted = null;
        h.View.SortRequested += (_, id) => sorted = id;

        double x = HeaderCenterX(1);
        h.Window.MouseDown(HeaderPoint(x), MouseButton.Left);
        h.Window.MouseMove(HeaderPoint(x + 2), RawInputModifiers.LeftMouseButton);
        h.Window.MouseUp(HeaderPoint(x + 2), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before[1], sorted);
        Assert.Equal(before, Columns.VisibleColumns.Select(c => c.Id).ToList());
    }

    [AvaloniaFact]
    public void DraggingAHeaderPastTheThresholdReordersAndDoesNotSort()
    {
        using var h = new Harness(5);
        var before = Columns.VisibleColumns.Select(c => c.Id).ToList();
        string? sorted = null;
        h.View.SortRequested += (_, id) => sorted = id;

        // Drag column 0 to the right, dropping it past column 2's midpoint.
        double from = HeaderCenterX(0);
        double to   = HeaderCenterX(2) + 1;
        h.Window.MouseDown(HeaderPoint(from), MouseButton.Left);
        h.Window.MouseMove(HeaderPoint(from + 20), RawInputModifiers.LeftMouseButton);
        h.Window.MouseMove(HeaderPoint(to), RawInputModifiers.LeftMouseButton);
        h.Window.MouseUp(HeaderPoint(to), MouseButton.Left);
        Dispatcher.UIThread.RunJobs(); // Reorder is posted, not run inline

        Assert.Null(sorted);
        var after = Columns.VisibleColumns.Select(c => c.Id).ToList();
        Assert.NotEqual(before, after);
        Assert.Equal(new[] { before[1], before[2], before[0] }, after.Take(3).ToArray());
    }

    // A drag that ends where it began - same gap index - is a no-op reorder,
    // and still must not fall through to sorting.
    [AvaloniaFact]
    public void DraggingAHeaderBackToItsOwnSlotChangesNothingAndDoesNotSort()
    {
        using var h = new Harness(5);
        var before = Columns.VisibleColumns.Select(c => c.Id).ToList();
        string? sorted = null;
        h.View.SortRequested += (_, id) => sorted = id;

        double x = HeaderCenterX(1);
        h.Window.MouseDown(HeaderPoint(x), MouseButton.Left);
        h.Window.MouseMove(HeaderPoint(x + 30), RawInputModifiers.LeftMouseButton);
        h.Window.MouseMove(HeaderPoint(x), RawInputModifiers.LeftMouseButton);
        h.Window.MouseUp(HeaderPoint(x), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(sorted);
        Assert.Equal(before, Columns.VisibleColumns.Select(c => c.Id).ToList());
    }

    // Dropping past the right edge of every column puts the dragged one last.
    [AvaloniaFact]
    public void DraggingAHeaderPastTheEndPutsItLast()
    {
        using var h = new Harness(5);
        var before = Columns.VisibleColumns.Select(c => c.Id).ToList();

        double from = HeaderCenterX(0);
        double past = TrackRowViewModel.ArtColumnWidth + Columns.VisibleColumns.Sum(c => c.Width) + 50;
        h.Window.MouseDown(HeaderPoint(from), MouseButton.Left);
        h.Window.MouseMove(HeaderPoint(from + 20), RawInputModifiers.LeftMouseButton);
        h.Window.MouseMove(HeaderPoint(past), RawInputModifiers.LeftMouseButton);
        h.Window.MouseUp(HeaderPoint(past), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var after = Columns.VisibleColumns.Select(c => c.Id).ToList();
        Assert.Equal(before[0], after[^1]);
        Assert.Equal(before.Skip(1).ToList(), after.Take(after.Count - 1).ToList());
    }

    // Right-clicking a header must not start a column drag at all.
    [AvaloniaFact]
    public void RightClickingAHeaderNeitherSortsNorReorders()
    {
        using var h = new Harness(5);
        var before = Columns.VisibleColumns.Select(c => c.Id).ToList();
        string? sorted = null;
        h.View.SortRequested += (_, id) => sorted = id;

        double x = HeaderCenterX(1);
        h.Window.MouseDown(HeaderPoint(x), MouseButton.Right);
        h.Window.MouseUp(HeaderPoint(x), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(sorted);
        Assert.Equal(before, Columns.VisibleColumns.Select(c => c.Id).ToList());
    }
}
