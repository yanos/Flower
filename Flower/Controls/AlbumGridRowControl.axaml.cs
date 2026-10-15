using System;
using System.ComponentModel;

using Avalonia.Controls;

using Flower.ViewModels;

namespace Flower.Controls;

// One row of tiles (+ an optional expanded track list below them, drawn by
// AlbumTrackList, which handles its own clicks, keys and menus) in
// AlbumGridView's grid - see AlbumGridRowViewModel. Instances are recycled by
// UniformRowPanel as the user scrolls, so DataContext changes
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

    // The height ExpansionBorder is growing or shrinking to - its Height
    // property reads the transition's value of the moment, not where it is
    // headed. AlbumGridView scrolls to what the row will be, not what it is
    // mid-animation (RevealExpanded).
    private double _expansionHeight;

    public AlbumGridRowControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // Where the songs will end once the expansion has finished opening,
    // measured from the top of this row.
    public double ExpandedBottom => ExpansionBorder.Bounds.Top + _expansionHeight;

    // How tall the songs are drawn right now, mid-animation included - what a
    // row closing above another takes away from everything under it.
    public double CurrentExpansionHeight => ExpansionBorder.Bounds.Height;

    // A row handed another album's row while scrolling - or a new row object
    // after the grid rebuilds - is shown at its height outright. Only the row
    // the user opens or closes animates: a recycled row growing in place as it
    // scrolls on screen pushes everything under it down mid-scroll.
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_row != null)
            _row.PropertyChanged -= Row_PropertyChanged;

        _row = DataContext as AlbumGridRowViewModel;

        if (_row != null)
            _row.PropertyChanged += Row_PropertyChanged;

        var transitions = ExpansionBorder.Transitions;
        ExpansionBorder.Transitions = null;
        UpdateExpansionHeight();
        ExpansionBorder.Transitions = transitions;
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
    //
    // Scrolling the songs into view is not this row's business: it cannot
    // tell the user opening it from the grid rebuilding or recycling it, and
    // used to scroll on all three - see AlbumGridView.RevealExpanded.
    private void UpdateExpansionHeight()
    {
        if (_row is not { IsExpanded: true } row)
        {
            _expansionHeight = 0;
            ExpansionBorder.Height = 0;
            return;
        }

        var rowCount = Math.Max(row.Column1Tracks.Count, row.Column2Tracks.Count);
        _expansionHeight = Math.Max(rowCount, 1) * TrackRowHeight + ExpansionCardVerticalPadding;
        ExpansionBorder.Height = _expansionHeight;
    }
}
