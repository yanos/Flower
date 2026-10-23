using System;
using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.ViewModels;

namespace Flower.Controls;

// One row of tiles (+ an optional expanded album below them, its songs drawn
// by AlbumTrackList, which handles its own clicks, keys and menus) in
// AlbumGridView's grid - see AlbumGridRowViewModel. Instances are recycled by
// UniformRowPanel as the user scrolls, so DataContext changes
// repeatedly on the same control - the PropertyChanged subscription below is
// swapped, not just added, each time to avoid leaking one per recycle.
public partial class AlbumGridRowControl : UserControl
{
    // The album card's "..." was pressed. Bubbles to MainView, which opens the
    // album menu over the album the card shows; the row is the source.
    public static readonly RoutedEvent<RoutedEventArgs> AlbumMenuRequestedEvent =
        RoutedEvent.Register<AlbumGridRowControl, RoutedEventArgs>(nameof(AlbumMenuRequested), RoutingStrategies.Bubble);

    public event EventHandler<RoutedEventArgs> AlbumMenuRequested
    {
        add => AddHandler(AlbumMenuRequestedEvent, value);
        remove => RemoveHandler(AlbumMenuRequestedEvent, value);
    }

    private AlbumGridRowViewModel? _row;

    public AlbumGridRowControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // Where the album card will end once it has finished opening, measured
    // from the top of this row - AlbumGridView scrolls to what the row will
    // be, not what it is mid-animation (RevealExpanded).
    public double ExpandedBottom => Expansion.Bounds.Top + Expansion.FullHeight;

    // How tall the card is drawn right now, mid-animation included - what a
    // row closing above another takes away from everything under it.
    public double CurrentExpansionHeight => Expansion.Bounds.Height;

    // A row handed another album's row while scrolling - or a new row object
    // after the grid rebuilds - is shown open or closed outright. Only the row
    // the user opens or closes animates: a recycled row growing in place as it
    // scrolls on screen pushes everything under it down mid-scroll.
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_row != null)
            _row.PropertyChanged -= Row_PropertyChanged;

        _row = DataContext as AlbumGridRowViewModel;

        if (_row != null)
            _row.PropertyChanged += Row_PropertyChanged;

        var transitions = Expansion.Transitions;
        Expansion.Transitions = null;
        ShowExpansion();
        Expansion.Transitions = transitions;
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlbumGridRowViewModel.IsExpanded))
            ShowExpansion();
    }

    // Scrolling the album into view is not this row's business: it cannot
    // tell the user opening it from the grid rebuilding or recycling it, and
    // used to scroll on all three - see AlbumGridView.RevealExpanded.
    private void ShowExpansion() => Expansion.Reveal = _row?.IsExpanded == true ? 1 : 0;

    private void Play_Click(object? sender, RoutedEventArgs e) => Play(shuffle: false);

    private void Shuffle_Click(object? sender, RoutedEventArgs e) => Play(shuffle: true);

    private void Play(bool shuffle)
    {
        if (_row is { ExpandedTracks.Count: > 0 } row)
            this.FindDataContext<MainViewModel>()?.PlayTracks(row.ExpandedTracks, shuffle);
    }

    // Every song of the album not yet here, behind the tile's own icon.
    private void Download_Requested(object? sender, DownloadIndicatorViewModel indicator)
    {
        if (_row is { ExpandedTracks.Count: > 0 } row && this.FindDataContext<MainViewModel>() is { } main)
            _ = main.Downloads.DownloadAlbumAsync(indicator, row.ExpandedTracks);
    }

    private void Menu_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RaiseEvent(new RoutedEventArgs(AlbumMenuRequestedEvent, sender as Control ?? this));
    }
}
