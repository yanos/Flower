using System;
using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Input;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Models;
using Flower.ViewModels;

namespace Flower.Controls;

// One row of tiles (+ an optional expanded track list below them) in
// AlbumGridView's grid - see AlbumGridRowViewModel. Instances are recycled by
// VirtualizingStackPanel as the user scrolls, so DataContext changes
// repeatedly on the same control - the PropertyChanged subscription below is
// swapped, not just added, each time to avoid leaking one per recycle.
public partial class AlbumGridRowControl : UserControl
{
    // Rough per-track-row height (Padding="6,4" + FontSize="12" text in the
    // XAML template) - doesn't need to be pixel-perfect, ExpansionBorder's
    // ClipToBounds hides any minor overshoot/undershoot, but it drives the
    // animated target Height so should be close.
    private const double TrackRowHeight = 26;

    // ExpansionCard's own Padding="12,10" (top+bottom = 20) - added on top of
    // the track rows themselves since it sits *inside* the animated
    // ExpansionBorder, not outside it (see AlbumGridRowControl.axaml).
    private const double ExpansionCardVerticalPadding = 20;

    private AlbumGridRowViewModel? _row;

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
            return;
        }

        var rowCount = Math.Max(row.Column1Tracks.Count, row.Column2Tracks.Count);
        ExpansionBorder.Height = Math.Max(rowCount, 1) * TrackRowHeight + ExpansionCardVerticalPadding;
    }

    private void TrackRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Border)?.DataContext is Track track)
            Ioc.Default.GetService<MainViewModel>()?.PlayTrack(track);
    }
}
