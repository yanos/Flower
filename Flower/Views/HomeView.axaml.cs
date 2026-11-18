using System;
using System.ComponentModel;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.ViewModels;

namespace Flower.Views;

public partial class HomeView : UserControl
{
    // The Albums grid's cell: a 180 tile and its 8 either side.
    private const double CellWidth = 180 + 16;

    private readonly HomeShelfRowsHost _rows;
    private HomeViewModel? _home;
    private IDisposable? _reveal;

    // For a test to step the covers' slide by hand.
    internal HomeShelfRowsHost ShelfRows => _rows;

    public HomeView()
    {
        InitializeComponent();
        _rows = new HomeShelfRowsHost(Rows, width => HomeShelfRowsHost.DesktopColumnsFor(width, CellWidth),
            songColumns: 2, historyLink: true);
        DataContextChanged += (_, _) => Attach(DataContext as HomeViewModel);
        // Scrolling by hand mid-reveal takes over from it.
        Scroller.AddHandler(PointerWheelChangedEvent, (_, _) => StopReveal(), RoutingStrategies.Tunnel);
    }

    private void Attach(HomeViewModel? home)
    {
        if (_home != null)
            _home.PropertyChanged -= Home_PropertyChanged;
        _home = home;
        if (_home != null)
            _home.PropertyChanged += Home_PropertyChanged;
    }

    // A Recently Added album opened: scrolled so its songs show, as the
    // Albums grid scrolls one (ExpansionReveal). Posted, as the card's own
    // opening is, so the card has its album when it is measured.
    private void Home_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HomeViewModel.ExpandedAlbumKey))
            return;
        if (_home?.ExpandedAlbumKey == null)
        {
            StopReveal();
            return;
        }

        Dispatcher.UIThread.Post(RevealOpened);
    }

    private void RevealOpened()
    {
        StopReveal();
        Scroller.UpdateLayout();

        var cards = Rows.GetVisualDescendants().OfType<AlbumExpansionCard>().Where(c => c.IsEffectivelyVisible).ToList();
        if (cards.FirstOrDefault(c => c.IsOpen) is not { } card || card.GetVisualParent() is not Control row
            || card.TranslatePoint(default, Scroller) is not { } at)
            return;

        // What a card closing above the one opening still holds, read now
        // rather than when it was told to close, as the Albums grid reads it
        // (AlbumGridView.RevealExpanded) - it gives that back to everything
        // under it as it folds.
        var shift = cards
            .Where(c => !c.IsOpen && c.CurrentHeight > 0 && c.TranslatePoint(default, Scroller) is { } p && p.Y < at.Y)
            .Sum(c => c.CurrentHeight);
        _reveal = ExpansionReveal.Start(Scroller, row, card.Bounds.Top + card.ExpandedBottom, shift);
    }

    private void StopReveal()
    {
        _reveal?.Dispose();
        _reveal = null;
    }

    // A Recently Played song's menu, from its "..." or a right-click - the
    // song menu MainView builds for the track list.
    private void Song_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: TrackRowViewModel row } song)
        {
            this.FindAncestorOfType<MainView>()?.OpenRecentlyPlayedMenu(row, song);
            e.Handled = true;
        }
    }

    private void SongMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TrackRowViewModel row } button)
            this.FindAncestorOfType<MainView>()?.OpenRecentlyPlayedMenu(row, button);
    }

    private void Song_DownloadRequested(object? sender, DownloadIndicatorViewModel indicator)
    {
        if (indicator is TrackRowViewModel row && this.FindDataContext<MainViewModel>() is { } vm)
            _ = vm.Downloads.DownloadRowAsync(row);
    }
}
