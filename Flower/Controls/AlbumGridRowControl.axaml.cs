using System;
using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Threading;

using Flower.ViewModels;

namespace Flower.Controls;

// One row of tiles (+ an optional expanded track list below them, drawn by
// AlbumTrackList, which handles its own clicks, keys and menus) in
// AlbumGridView's grid - see AlbumGridRowViewModel. Instances are recycled by
// VirtualizingStackPanel as the user scrolls, so DataContext changes
// repeatedly on the same control - the PropertyChanged subscription below is
// swapped, not just added, each time to avoid leaking one per recycle.
public partial class AlbumGridRowControl : UserControl
{
    // Rough per-track-row height - each row is the Panel's Margin (3 top + 3
    // bottom) around the text Grid's own Margin (6 + 6) around FontSize="13"
    // text (~16), i.e. ~34 - see AlbumTrackList's row template. Doesn't need to be
    // pixel-perfect: ExpansionBorder's ClipToBounds hides any minor overshoot/
    // undershoot, but it drives the animated target Height so should be close.
    // Slightly overestimates (the last row has no trailing gap to actually
    // account for), which just leaves a little harmless empty space at the
    // bottom rather than clipping anything.
    private const double TrackRowHeight = 34;

    // ExpansionCard's own Padding="12,10" (top+bottom = 20) - added on top of
    // the track rows themselves since it sits *inside* the animated
    // ExpansionBorder, not outside it (see AlbumGridRowControl.axaml).
    private const double ExpansionCardVerticalPadding = 20;

    private AlbumGridRowViewModel? _row;
    private DispatcherTimer? _scrollIntoViewTimer;

    public AlbumGridRowControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_row != null)
            _row.PropertyChanged -= Row_PropertyChanged;

        _row = DataContext as AlbumGridRowViewModel;

        if (_row != null)
            _row.PropertyChanged += Row_PropertyChanged;

        UpdateExpansionHeight();
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AlbumGridRowViewModel.IsExpanded) or nameof(AlbumGridRowViewModel.ExpandedTracks))
            UpdateExpansionHeight();
    }

    // ExpansionBorder's Height is set here rather than bound in XAML - it
    // needs a concrete pixel target (not "Auto") for the DoubleTransition on
    // Height to actually animate smoothly between two real values, which is
    // the whole point of precomputing it from the (small, bounded) track
    // count instead of just letting the two-column content measure itself.
    private void UpdateExpansionHeight()
    {
        if (_row is not { IsExpanded: true } row)
        {
            ExpansionBorder.Height = 0;
            _scrollIntoViewTimer?.Stop();
            return;
        }

        var rowCount = Math.Max(row.Column1Tracks.Count, row.Column2Tracks.Count);
        ExpansionBorder.Height = Math.Max(rowCount, 1) * TrackRowHeight + ExpansionCardVerticalPadding;

        ScheduleScrollIntoView();
    }

    // Waits out ExpansionBorder's own 220ms Height transition (see this
    // control's own XAML) before scrolling - calling BringIntoView
    // immediately would measure against the pre-animation (near-zero)
    // height, under-scrolling and leaving the newly revealed tracks cut off
    // at the bottom of the viewport instead of fully visible once expanded.
    private void ScheduleScrollIntoView()
    {
        _scrollIntoViewTimer?.Stop();
        _scrollIntoViewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _scrollIntoViewTimer.Tick += ScrollIntoViewTimer_Tick;
        _scrollIntoViewTimer.Start();
    }

    private void ScrollIntoViewTimer_Tick(object? sender, EventArgs e)
    {
        _scrollIntoViewTimer?.Stop();
        _scrollIntoViewTimer = null;
        ExpansionBorder.BringIntoView();
    }
}
