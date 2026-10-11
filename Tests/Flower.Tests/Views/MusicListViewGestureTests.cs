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

namespace Flower.Tests.Views;

// What only a shown MusicListView can show about its pointer gestures: the
// TwoWay SelectedTrack echo guard, a press below the last row, and one header
// click and one header drag end to end - the pointer capture that carries a
// drag past the cell it started on, and the reorder posted rather than run
// inside the release. The rules themselves are TrackRowSelectionTests' and
// ColumnHeaderDragTests'.
//
// These drive the real gesture through a shown headless window's input
// pipeline (HeadlessWindowExtensions.MouseDown/MouseMove/MouseUp), not by
// calling the private handlers.
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


    private sealed class Harness : IDisposable
    {
        public Window Window { get; }
        public MusicListView View { get; }
        public List<TrackRowViewModel> Items { get; }

        public Harness(int rowCount)
        {
            View   = new MusicListView();
            Items  = Rows(rowCount);
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
    public void ClickBelowTheLastRowLeavesTheSelectionAlone()
    {
        using var h = new Harness(4);

        h.Click(1);
        h.Window.MouseDown(new Point(200, HeaderHeight + 20 * RowHeight), MouseButton.Left);
        h.Window.MouseUp(new Point(200, HeaderHeight + 20 * RowHeight), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { 1 }, h.SelectedIndices());
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
}
