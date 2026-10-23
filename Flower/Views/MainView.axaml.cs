using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using CommunityToolkit.Mvvm.DependencyInjection;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Controls;
using Flower.Logging;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

using Material.Icons;
using Material.Icons.Avalonia;

namespace Flower.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _viewModel;

    // From the container's factory the view model carries: this view is built
    // by XAML, so nothing injects into it. Silent until it has a view model,
    // which is before anything here has anything to say.
    private ILogger Logger =>
        _viewModel?.LoggerFactory.CreateLogger<MainView>() ?? NullLogger<MainView>.Instance;

    private ContextMenu _columnMenu = new();
    private readonly ContextMenu _sidebarItemMenu = new();
    private SidebarItem? _dropTargetPlaylistItem;

    // Drag album/artist names from SubList onto a sidebar playlist - mirrors
    // MusicListView's own cross-drag fields/gesture (see OnTrackDragMoved/
    // OnTrackDragEnded) but lives here since SubList is a stock ListBox with no
    // hand-rolled panel to raise that event itself. The gesture's own state
    // machine (selection algebra, anchor, drag threshold) lives in
    // NameSelectionDragGesture, shared with both album grids below; only the
    // hit-testing and the ghost/drop visuals stay here.
    private NameSelectionDragGesture? _subListGesture;
    private bool _syncingSubListSelection; // guards against feedback loops between SubList and the VM

    private IDisposable? _spin;
    private RotateTransform? _spinTransform;

    // Takes the status bar back down a second after its last message - see
    // HideStatusBarSoon.
    private DispatcherTimer? _statusHideTimer;

    // Per-view (Songs / album / artist / playlist) scroll position + selection memory
    private readonly Dictionary<string, ViewScrollState> _viewStates = new();
    private string? _currentViewKey;

    private readonly record struct ViewScrollState(double ScrollOffsetY, string? SelectedTrackPath);

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ActualThemeVariantChanged += (_, _) =>
        {
            _playerBarOnArt = null;
            ApplyPlayerBarTint();
        };
        PlaybackControls.PlayOrPauseRequested += (_, _) => _viewModel?.PlayOrPauseFromCurrentView();

        // Wire MusicListView events
        MusicList.RowActivated    += OnRowActivated;
        MusicList.RowContextMenu  += OnRowContextMenu;
        MusicList.RowDownloadRequested += OnRowDownloadRequested;
        MusicList.HeaderContextMenu += OnHeaderContextMenu;
        MusicList.SortRequested   += OnSortRequested;
        MusicList.RowReordered    += OnRowReordered;
        MusicList.TrackDragMoved  += OnTrackDragMoved;
        MusicList.TrackDragEnded  += OnTrackDragEnded;

        // Forward Space from inside the list
        MusicList.AddHandler(KeyDownEvent, MusicList_KeyDown, RoutingStrategies.Tunnel);

        // Songs dragged in from Finder or Explorer, anywhere on the window -
        // see MainViewModel.AddDroppedSongsAsync. The window's own drags
        // (rows, albums onto playlists) are pointer gestures rather than
        // DragDrop, so nothing else here is a drop target.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnSongsDragOver);
        AddHandler(DragDrop.DropEvent, OnSongsDropped);

        SidebarList.ContextRequested += SidebarList_ContextRequested;

        // Drag album/artist names from SubList onto a sidebar playlist. Also
        // fully owns SubList's click-to-select behavior (see SubList_PointerPressed)
        // rather than relying on ListBoxItem's native SelectionMode="Multiple"
        // handling, which doesn't give the plain-click-collapses-the-rest /
        // Ctrl-toggle / Shift-range semantics this needs. Registered on the
        // Tunnel phase - not Bubble - so this runs BEFORE ListBoxItem's own
        // PointerPressed handling and can mark the event Handled to suppress it
        // entirely; a Bubble handler (even with handledEventsToo) would only ever
        // see the press after native selection processing already ran.
        SubList.AddHandler(PointerPressedEvent, SubList_PointerPressed, RoutingStrategies.Tunnel);
        SubList.PointerMoved += SubList_PointerMoved;
        SubList.PointerReleased += SubList_PointerReleased;
        SubList.PointerCaptureLost += SubList_PointerCaptureLost;

        // Same gesture as SubList above (click/Ctrl-toggle/Shift-range select,
        // drag onto a sidebar playlist), retargeted at the tile grid - see
        // AlbumGrid_PointerPressed. Not a stock control with native selection
        // to suppress, so plain Bubble wiring is enough, no Tunnel trick needed.
        AlbumGrid.PointerPressed += AlbumGrid_PointerPressed;
        AlbumGrid.PointerMoved += AlbumGrid_PointerMoved;
        AlbumGrid.PointerReleased += AlbumGrid_PointerReleased;
        AlbumGrid.PointerCaptureLost += AlbumGrid_PointerCaptureLost;
        // AlbumTileControl handles no pointer gesture of its own except its
        // "..." button (see its own doc comment) - right-click bubbles
        // up from whichever tile the pointer landed on the same way the
        // four handlers above do, resolved the same way via HitTestTile.
        AlbumGrid.ContextRequested += AlbumGrid_ContextRequested;

        // The "..." on every album tile - the grid's and Home's (AlbumCard).
        AddHandler(AlbumCard.MenuRequestedEvent, AlbumCard_MenuRequested);
        AddHandler(AlbumGridRowControl.AlbumMenuRequestedEvent, AlbumGridRow_AlbumMenuRequested);

        // Home's covers: a right-click opens the tile's menu, as the "..."
        // does, and a drag carries the album to the sidebar. Each cover is a
        // Button, which takes the press and captures the pointer for its
        // click, so the press and the moves are watched handled or not, and
        // the release on the way down, before the Button can click.
        AddHandler(ContextRequestedEvent, Home_ContextRequested);
        AddHandler(PointerPressedEvent, Home_PointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, Home_PointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, Home_PointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, Home_PointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);

        // The window-wide shortcuts (Space, Cmd/Ctrl+, L, J, I) are handled on
        // the window itself rather than here - see OnAttachedToVisualTree.

        // See MainView_PreviewPointerPressed for why this can't just rely on LostFocus.
        AddHandler(PointerPressedEvent, MainView_PreviewPointerPressed, RoutingStrategies.Tunnel);
    }

    // Cmd/Ctrl+, (Settings), Space and the rest must work whichever control
    // has focus - and when none does. They used to be handled at this view's
    // root, which is not enough: with nothing focused (a click on empty
    // space, a native menu just closed) Avalonia routes a key to the window
    // itself, the route never reaches this view, and Space did nothing while
    // the app was plainly in front. Tunnelling on the window catches every
    // key it is sent, before any control under it.
    private TopLevel? _shortcutHost;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _shortcutHost = TopLevel.GetTopLevel(this);
        _shortcutHost?.AddHandler(KeyDownEvent, MainView_PreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _shortcutHost?.RemoveHandler(KeyDownEvent, MainView_PreviewKeyDown);
        _shortcutHost = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.BrowserPairing.PropertyChanged -= OnBrowserPairingPropertyChanged;
            _viewModel.SettingsRequested -= OnSettingsRequested;
            _viewModel.ColumnSelectorRequested -= OnColumnSelectorRequested;
            _viewModel.LogWindowRequested -= OnLogWindowRequested;
            _viewModel.EqualizerWindowRequested -= OnEqualizerWindowRequested;
            _viewModel.NavigateToTrackRequested -= OnNavigateToTrackRequested;
            _viewModel.AlbumRevealRequested -= OnAlbumRevealRequested;
            _viewModel.Home.AlbumOpened -= OnHomeAlbumOpened;
            _viewModel.Queue.PropertyChanged -= OnQueuePropertyChanged;
            _viewModel.PlaylistConflictRequested -= OnPlaylistConflictRequested;
            _viewModel.RenamePlaylistRequested -= OnRenamePlaylistRequested;
            _viewModel.DeletePlaylistConfirmationRequested -= OnDeletePlaylistConfirmationRequested;
            _viewModel.SmartPlaylistEditorRequested -= OnSmartPlaylistEditorRequested;
            _viewModel.NowPlaying.PropertyChanged -= OnNowPlayingPropertyChanged;
            HideStatusBar();
        }

        _viewModel = DataContext as MainViewModel;
        CreateNameGestures(_viewModel);

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.BrowserPairing.PropertyChanged += OnBrowserPairingPropertyChanged;
            _viewModel.Queue.PropertyChanged += OnQueuePropertyChanged;
            _viewModel.SettingsRequested += OnSettingsRequested;
            _viewModel.ColumnSelectorRequested += OnColumnSelectorRequested;
            _viewModel.LogWindowRequested += OnLogWindowRequested;
            _viewModel.EqualizerWindowRequested += OnEqualizerWindowRequested;
            _viewModel.NavigateToTrackRequested += OnNavigateToTrackRequested;
            _viewModel.AlbumRevealRequested += OnAlbumRevealRequested;
            _viewModel.Home.AlbumOpened += OnHomeAlbumOpened;
            _viewModel.PlaylistConflictRequested += OnPlaylistConflictRequested;
            _viewModel.RenamePlaylistRequested += OnRenamePlaylistRequested;
            _viewModel.DeletePlaylistConfirmationRequested += OnDeletePlaylistConfirmationRequested;
            _viewModel.SmartPlaylistEditorRequested += OnSmartPlaylistEditorRequested;
            _viewModel.NowPlaying.PropertyChanged += OnNowPlayingPropertyChanged;
            BuildColumnMenu();
            ShowStatusMessage(_viewModel.BusyMessage);

            // Reflect the ViewModel's (possibly persisted) sort state immediately -
            // MusicList's own SortColumn/SortAscending fields default to
            // "TrackNumber"/ascending and only update via the PropertyChanged handler
            // below, which won't fire for a value the ViewModel already had before
            // this handler was attached.
            MusicList.UpdateSortIndicators(_viewModel.SortColumn, _viewModel.SortAscending);

            // Seeds _viewStates with whatever scroll position was persisted
            // last session (see MainViewModel.SaveLastView) so the *first*
            // real view-switch detection in ApplyRows below - which, at
            // startup, is the background rescan's own Rows population, not
            // this call (Rows is always still empty this early) - finds it
            // and restores scroll instead of defaulting to 0.
            SeedRestoredViewState();

            // Push initial rows to MusicListView - a no-op in the normal
            // startup case (Rows is empty here; the real initial push happens
            // below, once Rows' own PropertyChanged fires after the
            // background rescan actually populates it), but still needed for
            // the rarer case for this DataContext already having Rows (e.g.
            // MainView re-attaching to an already-initialized ViewModel).
            if (_viewModel.Rows.Count > 0)
                ApplyRows();

            UpdateServerSettingsPane();
        }

        ApplyPlayerBarTint();
    }

    private void OnNowPlayingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CurrentlyPlayingControlViewModel.AlbumArt))
            ApplyPlayerBarTint();
    }

    // The player bar washed in the album's colour, the way the phone's Now
    // Playing sheet is (NowPlayingView): ArtBackdrop's colour and gradient,
    // and with no cover - nothing playing included - the same fallback. Everything on the bar
    // is then drawn in the colour for things over a cover's (PaletteOnArt),
    // as everything on that sheet is: the colours the bar's controls are
    // drawn with are restated for the bar alone (PlayerBarOnArt), so the
    // controls themselves do not know which way they are being drawn.
    private ResourceDictionary? _playerBarOnArt;

    private void ApplyPlayerBarTint()
    {
        var merged = PlayerBar.Resources.MergedDictionaries;
        if (_viewModel is not { TintPlayerBarWithAlbumColor: true } vm)
        {
            merged.Clear();
            PlayerBar.ClearValue(Border.BackgroundProperty);
            RoutePicker.GlyphColor = null;
            return;
        }

        _playerBarOnArt ??= PlayerBarOnArt();
        if (!merged.Contains(_playerBarOnArt))
        {
            merged.Clear();
            merged.Add(_playerBarOnArt);
        }
        PlayerBar.Background = ArtBackdrop.SheetBrush(ArtBackdrop.DominantColor(vm.NowPlaying.AlbumArt));
        RoutePicker.GlyphColor = OnArtColor();
    }

    private object? AppResource(string key) =>
        Application.Current?.TryGetResource(key, ActualThemeVariant, out var value) == true ? value : null;

    private Color OnArtColor() => AppResource("PaletteOnArt") is Color color ? color : default;

    // What each colour the bar's controls name is on a cover's colour - the
    // phone's Now Playing's choices (NowPlayingView.axaml), every one of them
    // Theme.axaml's AppOnArt* on PaletteOnArt: words and glyphs in it, the
    // artist line and the times dimmer, shuffle and repeat half as bright off
    // and full on, a press dimming it rather than turning it the accent, the
    // ring round play in it, and the sliders' rails faint. Hover is new here
    // - a phone has none - and dims a little less than a press. The divider
    // is gone: the bar's colour already sets it apart from the page. Built
    // for the theme in use, and again when it changes.
    private ResourceDictionary PlayerBarOnArt() => new()
    {
        ["PaletteTextPrimary"] = OnArtColor(),
        ["AppTextPrimaryBrush"] = AppResource("AppOnArtBrush"),
        ["AppTextSecondaryBrush"] = AppResource("AppOnArtSecondaryBrush"),
        ["AppTextDisabledBrush"] = AppResource("AppOnArtMutedBrush"),
        ["AppAccentBrush"] = AppResource("AppOnArtBrush"),
        ["AppAccentHoverBrush"] = AppResource("AppOnArtHoverBrush"),
        ["AppAccentPressedBrush"] = AppResource("AppOnArtRingBrush"),
        ["AppChromeBrush"] = AppResource("AppOnArtBrush"),
        ["AppChromeBorderBrush"] = AppResource("AppOnArtBrush"),
        ["AppDividerBrush"] = Brushes.Transparent,
        ["SliderTrackFill"] = AppResource("AppOnArtTrackBrush"),
        ["SliderTrackFillPointerOver"] = AppResource("AppOnArtTrackBrush"),
        ["SliderTrackFillPressed"] = AppResource("AppOnArtTrackBrush"),
    };

    // The three name-selection gestures all read and write the same
    // MainViewModel.SelectedSubItems, so they only exist while there is a
    // view model to point them at - rebuilt from scratch on every DataContext
    // change rather than re-targeted, since their Shift+click anchors mean
    // nothing across a different library scope anyway.
    private void CreateNameGestures(MainViewModel? vm)
    {
        if (vm == null)
        {
            _subListGesture = null;
            _albumGridGesture = null;
            return;
        }

        _subListGesture = new NameSelectionDragGesture(
            () => vm.VisibleSubListItems,
            () => vm.SelectedSubItems,
            vm.SetSelectedSubItems,
            selectOnPlainPress: true);

        _albumGridGesture = new NameSelectionDragGesture(
            () => vm.AlbumGridTiles.Select(t => t.Name).ToList(),
            () => vm.SelectedSubItems,
            vm.SetSelectedSubItems,
            selectOnPlainPress: false);
    }

    // See OnDataContextChanged's own comment for why this only matters at
    // startup, not on an in-session view switch (ApplyRows' _viewStates
    // save/restore already covers that).
    private void SeedRestoredViewState()
    {
        if (_viewModel is not { WasLastViewRestored: true } vm)
            return;
        _viewStates[PageKey(vm.CurrentViewKey)] = new ViewScrollState(vm.LastScrollOffsetY, null);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsBusy):
                UpdateSpinner();
                if (!_viewModel!.IsBusy)
                    HideStatusBarSoon();
                break;
            case nameof(MainViewModel.BusyMessage):
                ShowStatusMessage(_viewModel!.BusyMessage);
                break;
            // Said like a step of work, and taken down the same way once it is
            // cleared - unless work is still going on, whose message it was
            // standing in front of.
            case nameof(MainViewModel.PlaybackNotice):
                if (_viewModel!.PlaybackNotice is { } notice)
                    ShowStatusMessage(notice);
                else if (_viewModel.IsBusy)
                    ShowStatusMessage(_viewModel.BusyMessage);
                else
                    HideStatusBarSoon();
                break;
            case nameof(MainViewModel.Rows):
                ApplyRows();
                break;
            case nameof(MainViewModel.TintPlayerBarWithAlbumColor):
                ApplyPlayerBarTint();
                break;
            case nameof(MainViewModel.SortColumn):
            case nameof(MainViewModel.SortAscending):
                ApplyReorderAndSort();
                break;
            case nameof(MainViewModel.SelectedSubItems):
                SyncSubListSelectionFromViewModel();
                break;
            // A new list of names (a rescan, or the filter) is a new
            // ItemsSource, which empties the ListBox's own selection while the
            // pick behind it stands - so the picked artist lost its highlight.
            // Posted, so the ListBox has taken the new items first.
            case nameof(MainViewModel.VisibleSubListItems):
                KeepAListedArtistPicked();
                Dispatcher.UIThread.Post(SyncSubListSelectionFromViewModel);
                break;
            case nameof(MainViewModel.SelectedServerSettings):
            case nameof(MainViewModel.IsSelectedDevicePaired):
            case nameof(MainViewModel.IsSelectedServerReachable):
            case nameof(MainViewModel.IsPairAwaitingApproval):
                UpdateServerSettingsPane();
                break;
            case nameof(MainViewModel.IsShowingServerSettings):
                UpdateServerSettingsPage();
                break;
        }
    }

    private void OnBrowserPairingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not BrowserPairingViewModel pairing)
            return;

        switch (e.PropertyName)
        {
            // The pairing screen is the whole page, and typing a code is the
            // one thing to do on it. Posted, so the box is visible - and so
            // focusable - by the time it is asked.
            case nameof(BrowserPairingViewModel.ShowsScreen):
                if (pairing.CanEnterCode)
                    Dispatcher.UIThread.Post(() => BrowserPairingCodeBox.Focus());
                break;
            // The box is disabled while a code is being checked, which takes
            // the keyboard focus off it - and nothing gave it back, so after a
            // refused code the next keystrokes went nowhere until the box was
            // clicked. Back into it, with the refused code selected so typing
            // replaces it.
            case nameof(BrowserPairingViewModel.IsPairing):
                if (!pairing.IsPairing && pairing.CanEnterCode)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        BrowserPairingCodeBox.Focus();
                        BrowserPairingCodeBox.SelectAll();
                    });
                }
                break;
        }
    }

    // The Artists page shows the artist picked in the column of names, so the
    // pick has to be one the column lists. Two ways it is not:
    //  - Artists restored at launch is selected before there are any names,
    //    so the pick a sidebar click makes (InitialSubItemForCurrentView)
    //    found nobody and the page sat empty. The first names make it instead.
    //  - The filter narrows the names and the artist on screen is no longer
    //    among them: the page moves to the first that is, the way the cards
    //    under it narrow to what the filter finds - and shows nobody when
    //    the filter finds no one.
    // Here rather than in the shared view model, because the phone sits on
    // Artists with nobody picked on purpose - that is its list of names.
    private void KeepAListedArtistPicked()
    {
        if (_viewModel is not { IsShowingArtistPage: true } vm)
            return;

        var listed = vm.VisibleSubListItems;
        var picked = vm.SelectedSubItems;
        if (picked.Count > 0 && picked.All(listed.Contains))
            return;

        if (listed.Count == 0)
        {
            if (picked.Count > 0 && !string.IsNullOrWhiteSpace(vm.FilterText))
                vm.SetSelectedSubItems([]);
            return;
        }

        var next = string.IsNullOrWhiteSpace(vm.FilterText)
            ? vm.Browser.InitialSubItemForCurrentView() ?? listed[0]
            : listed[0];
        vm.SetSelectedSubItems([next]);
    }

    // Pushes the ViewModel's SelectedSubItems set into SubList's own selection.
    // Guarded so the resulting SelectionChanged (fired by SubList.SelectedItems
    // mutation below) doesn't loop back into SetSelectedSubItems.
    private void SyncSubListSelectionFromViewModel()
    {
        if (_viewModel == null)
            return;
        _syncingSubListSelection = true;
        try
        {
            SubList.SelectedItems!.Clear();
            foreach (var item in _viewModel.SelectedSubItems)
                SubList.SelectedItems!.Add(item);
        }
        finally
        {
            _syncingSubListSelection = false;
        }
    }

    private void SubList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSubListSelection || _viewModel == null)
            return;
        _viewModel.SetSelectedSubItems(SubList.SelectedItems!.Cast<string>().ToList());
    }

    // ── Album grid: select / multi-select / drag-to-playlist ────────────────────
    // Same gesture and reasoning as SubList's own handlers above - see
    // SubList_PointerPressed's doc comment - just hit-testing AlbumGrid.Panel's
    // tiles (HitTestTile) instead of a ListBoxItem, and resolving the final
    // drag payload through the same MainViewModel.GetTracksForSubListItems
    // both share.

    // The same gesture as SubList's (see SubList_PointerPressed's doc comment
    // for the base rationale) - a plain click activates via
    // MainViewModel.ToggleAlbumExpandedCommand, and a Shift+click range runs
    // over the grid's alphabetical order (see MainViewModel.AlbumGridTiles),
    // driving MainViewModel.SelectedSubItems.
    private NameSelectionDragGesture? _albumGridGesture;

    // A plain (unmodified) press on a not-yet-selected tile might still turn
    // into a drag - see AlbumGrid_PointerMoved/Released. Toggling the
    // expansion immediately on press (what this used to do, back when a
    // plain click drilled into a whole separate track-list view rather than
    // expanding in place) hid the grid the instant you pressed down, before
    // a drag gesture ever had a chance to start. Deferred to release, and
    // only actually toggles if no drag occurred.
    private bool _albumGridPendingActivate;

    // Which tile that deferred click was on. The gesture only remembers a name,
    // which is all selection and drag need - but expanding needs the tile
    // itself (see AlbumTileKey).
    private AlbumTileViewModel? _albumGridPendingTile;

    private NameSelectionDragGesture? GestureFor(AlbumGridView grid) =>
        ReferenceEquals(grid, AlbumGrid) ? _albumGridGesture : null;


    private void AlbumGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not AlbumGridView grid || _viewModel is not { } vm)
            return;
        if (GestureFor(grid) is not { } gesture)
            return;
        if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed)
            return;
        var tile = grid.HitTestTile(e.Source);
        if (tile == null)
            return;

        bool shift  = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool toggle = e.KeyModifiers.HasFlag(PlatformShortcuts.Primary);

        // Double-click plays the album and (unlike a plain click's toggle)
        // always leaves it expanded, rather than the two plain clicks the OS
        // reports this as each toggling ToggleAlbumExpandedCommand in turn -
        // which would otherwise expand then immediately re-collapse it.
        if (e.ClickCount >= 2 && !shift && !toggle)
        {
            vm.PlayAlbum(tile);
            e.Handled = true;
            e.Pointer.Capture(null);
            EndAlbumGridDrag(gesture);
            return;
        }

        // A plain press deliberately leaves the selection alone (the gesture
        // is constructed with selectOnPlainPress: false) - the drag path
        // falls back to just this tile on its own if it isn't selected.
        bool alreadySelected = gesture.Press(tile.Name, e.GetPosition(grid), shift, toggle);

        e.Handled = true;

        _albumGridPendingActivate = !shift && !toggle && !alreadySelected;
        _albumGridPendingTile = _albumGridPendingActivate ? tile : null;
        e.Pointer.Capture(grid);
    }

    private void AlbumGrid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not AlbumGridView grid || GestureFor(grid) is not { } gesture)
            return;

        var pt = e.GetPosition(grid);
        if (!gesture.Move(pt))
            return;

        ShowDragGhost(grid, pt, DragGhostLabel(gesture.DragItems!));
        SetSidebarDropHighlight(HitTestSidebarDrop(grid, pt));
    }

    private void AlbumGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not AlbumGridView grid || GestureFor(grid) is not { } gesture)
            return;

        if (gesture is { IsDragging: true, DragItems: { } items })
            CompleteNameDrop(grid, e.GetPosition(grid), items);
        else if (_albumGridPendingActivate && _albumGridPendingTile is { } tile)
        {
            // No drag happened - a genuine plain click, now safe to
            // expand/collapse (see _albumGridPendingActivate's doc comment).
            _viewModel?.ToggleAlbumExpandedCommand?.Execute(tile);
        }

        e.Pointer.Capture(null);
        EndAlbumGridDrag(gesture);
    }

    private void AlbumGrid_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is AlbumGridView grid && GestureFor(grid) is { } gesture)
            EndAlbumGridDrag(gesture);
    }

    private void EndAlbumGridDrag(NameSelectionDragGesture gesture)
    {
        gesture.End();
        _albumGridPendingActivate = false;
        _albumGridPendingTile = null;
        ResetDragVisuals();
    }

    // Right-click on an album tile's art/name/artist (anywhere in
    // AlbumTileControl, which handles no pointer gesture of its own except its
    // download button - see its own doc comment) - resolved via the same
    // HitTestTile AlbumGrid_PointerPressed uses. Preserves the existing
    // multi-selection if the right-clicked tile is already part of it,
    // otherwise collapses to just this tile - same rule the track list's own
    // Panel_ContextRequested/TrackRow_ContextRequested already use, so a
    // right-click can act on a whole multi-selection of albums.
    private void AlbumGrid_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not AlbumGridView grid || _viewModel is not { } vm)
            return;
        var tile = grid.HitTestTile(e.Source);
        if (tile == null)
            return;

        if (!vm.SelectedSubItems.Contains(tile.Name))
            vm.SetSelectedSubItems(new List<string> { tile.Name });

        var tracks = vm.GetTracksForSubListItems(vm.SelectedSubItems).ToList();
        PlatformContextMenu.Open(BuildAlbumContextMenu(tracks, tile), grid);
        e.Handled = true;
    }

    // An album tile's "...": the menu a right-click on that tile opens. In the
    // grid that is the album menu over the selection, the way a right-click
    // there picks it (AlbumGrid_ContextRequested). On Home, a tile whose shelf
    // gives it a menu of its own (Continue Playing's, which can take it off the
    // shelf) opens that; any other opens the album menu over the album alone,
    // since Home has no selection of albums to act on.
    private void AlbumCard_MenuRequested(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not AlbumCard { Tile: { } tile } card || _viewModel is not { } vm)
            return;
        e.Handled = true;

        if (card.FindAncestorOfType<AlbumGridView>() != null)
        {
            if (!vm.SelectedSubItems.Contains(tile.Name))
                vm.SetSelectedSubItems(new List<string> { tile.Name });
            var selected = vm.GetTracksForSubListItems(vm.SelectedSubItems).ToList();
            PlatformContextMenu.Open(BuildAlbumContextMenu(selected, tile), card);
            return;
        }

        OpenHomeTileMenu(card, tile);
    }

    // The open album's "..." in the Albums grid: the album menu over that
    // album alone, as an album card's on Artists - the card is one album,
    // whatever tiles happen to be selected round it.
    private void AlbumGridRow_AlbumMenuRequested(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Control button
            || button.FindAncestorOfType<AlbumGridRowControl>(includeSelf: true)?.DataContext is not AlbumGridRowViewModel { ExpandedTile: { } tile } row)
            return;
        e.Handled = true;
        var tracks = row.ExpandedTracks.ToList();
        PlatformContextMenu.Open(BuildAlbumContextMenu(tracks, tile, () => OpenTrackInfo(tracks)), button);
    }

    private void OpenHomeTileMenu(AlbumCard card, AlbumTileViewModel tile)
    {
        // The tile's own menu, declared on its Button (Home's Continue
        // Playing) - opened for the Button, whose data its bindings are on.
        if (card.FindAncestorOfType<Button>() is { ContextMenu: { } own } owner)
        {
            PlatformContextMenu.Open(own, owner);
            return;
        }

        var tracks = LibraryBrowserViewModel.ExpandedTracksFor(tile).ToList();
        PlatformContextMenu.Open(BuildAlbumContextMenu(tracks, tile, () => OpenTrackInfo(tracks)), card);
    }

    // The cover a pointer event on Home landed on: the shelf's Button round
    // an AlbumCard - not the card's own "..." button inside it.
    private static (Button Tile, AlbumCard Card)? HomeTileAt(object? source)
    {
        if (source is not Visual visual)
            return null;
        var tile = visual.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("homeTile"));
        if (tile?.FindAncestorOfType<HomeView>() == null || tile.Content is not AlbumCard { Tile: not null } card)
            return null;
        return (tile, card);
    }

    // Recently Played's and Recently Added's covers, which declare no menu
    // of their own. Continue Playing's does, and Avalonia opens it first.
    private void Home_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || HomeTileAt(e.Source) is not { Card: { Tile: { } tile } card })
            return;
        OpenHomeTileMenu(card, tile);
        e.Handled = true;
    }

    // ── Drag an album from Home onto the sidebar ────────────────────────────────
    // A cover pressed and moved past the threshold carries its album, as a
    // grid tile does (AlbumGrid_PointerMoved): onto a playlist, the Playlists
    // space for a new one, or the Queue row. Released anywhere else it does
    // nothing - and since the pointer has left the cover, no click either.

    private AlbumTileViewModel? _homeDragTile;
    private Point _homeDragStart;
    private bool _homeDragging;

    private void Home_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        EndHomeDrag();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || HomeTileAt(e.Source) is not { Card.Tile: { } tile })
            return;
        _homeDragTile = tile;
        _homeDragStart = e.GetPosition(this);
    }

    private void Home_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_homeDragTile is not { } tile)
            return;

        var at = e.GetPosition(this);
        if (!_homeDragging)
        {
            var moved = at - _homeDragStart;
            if (moved.X * moved.X + moved.Y * moved.Y < NameSelectionDragGesture.DragThreshold * NameSelectionDragGesture.DragThreshold)
                return;
            _homeDragging = true;
        }

        ShowDragGhost(this, at, tile.Name);
        SetSidebarDropHighlight(HitTestSidebarDrop(this, at));
    }

    private void Home_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_homeDragTile is not { } tile || !_homeDragging)
        {
            EndHomeDrag();
            return;
        }

        var target = HitTestSidebarDrop(this, e.GetPosition(this));
        EndHomeDrag();
        CompleteTracksDrop(target, LibraryBrowserViewModel.ExpandedTracksFor(tile).ToList());

        // Kept from the cover, which would otherwise take the release as the
        // end of its click.
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // Only once dragging: a press itself moves the capture - from the
    // control under the pointer to the cover's Button - and that loss is the
    // gesture starting, not ending.
    private void Home_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_homeDragging)
            EndHomeDrag();
    }

    private void EndHomeDrag()
    {
        var wasDragging = _homeDragging;
        _homeDragTile = null;
        _homeDragging = false;
        if (wasDragging)
            ResetDragVisuals();
    }

    // An album card's play circle on the Artists page: the album from its
    // first song, queued on its own the way a double-click on one of its songs
    // queues it (MainViewModel.PlayTrackInAlbum).
    private void ArtistAlbumPlay_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ArtistAlbumSectionViewModel { ExpandedTracks: [var first, ..] } album)
            _viewModel?.PlayTrackInAlbum(album.ExpandedTracks, first);
    }

    // An album card's "...": the album menu over that album alone, as a Home
    // tile's opens it - the Artists page has no selection of albums either.
    private void ArtistAlbumMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button || button.DataContext is not ArtistAlbumSectionViewModel album)
            return;
        var tracks = album.ExpandedTracks.ToList();
        PlatformContextMenu.Open(BuildAlbumContextMenu(tracks, album.Tile, () => OpenTrackInfo(tracks)), button);
    }

    // ── Per-view scroll position + selection ────────────────────────────────────

    // The queue's rows on the Queue page, the browser's everywhere else - the
    // one list control draws either (see MainViewModel.IsShowingQueue).
    private IReadOnlyList<TrackRowViewModel> ShownRows =>
        _viewModel == null ? []
        : _viewModel.IsShowingQueue ? _viewModel.Queue.Rows
        : _viewModel.Rows;

    private void OnQueuePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel?.IsShowingQueue != true)
            return;
        if (e.PropertyName is nameof(QueueViewModel.Rows))
            ApplyRows();
        else if (e.PropertyName is nameof(QueueViewModel.IsReorderable))
            ApplyReorderAndSort();
    }

    // Not on a smart playlist: its order is whatever its rules produced,
    // so a drag would be undone by the next recompute - see
    // PlaylistManagementViewModel.ReorderTrack, which refuses it anyway.
    // On the queue, unless shuffle makes its order mean nothing - see
    // QueueViewModel.IsReorderable. The queue is in the order it plays in,
    // so no column shows a sort arrow there.
    private void ApplyReorderAndSort()
    {
        if (_viewModel == null)
            return;
        MusicList.AllowReorder = _viewModel.IsShowingQueue
            ? _viewModel.Queue.IsReorderable
            : _viewModel.SelectedSidebarItem is { Kind: SidebarItemKind.Playlist, Playlist: { IsSmart: false } };
        MusicList.UpdateSortIndicators(_viewModel.IsShowingQueue ? "" : _viewModel.SortColumn, _viewModel.SortAscending);
    }

    private void ApplyRows()
    {
        if (_viewModel == null)
            return;

        ApplyReorderAndSort();
        var rows = ShownRows;

        var newKey = PageKey(_viewModel.CurrentViewKey);

        // Only save/restore on an actual view switch — leave filtering/sorting
        // within the same view untouched.
        if (newKey == _currentViewKey)
        {
            MusicList.SetItems(rows);
            return;
        }

        // Keyed off the *old* key's own string shape (not the ViewModel's
        // current IsShowingAlbumGrid, which by
        // this point already reflect the *new* selection) so the outgoing
        // view's scroll is read from whichever control actually owned it.
        if (_currentViewKey != null)
            _viewStates[_currentViewKey] = new ViewScrollState(GetScrollOffsetYForKey(_currentViewKey), MusicList.SelectedRow?.Track.Path);

        MusicList.SetItems(rows);

        // The queue opens on the song playing, whatever it was left at, with
        // one of the songs that played before it showing above - the rest are
        // a scroll up (QueueViewModel.PlayedCount).
        if (_viewModel.IsShowingQueue)
        {
            MusicList.SelectedRow = null;
            MusicList.SetScrollOffsetY(Math.Max(0, (_viewModel.Queue.PlayedCount - 1) * TrackRowViewModel.RowHeight));
        }
        else if (_viewStates.TryGetValue(newKey, out var saved))
        {
            // Guarded: a null saved path would otherwise match the first
            // undownloaded placeholder in the view, which is not what was
            // selected last session - it is just the one track shape whose Path
            // is null. See MusicListView's own selection keys.
            MusicList.SelectedRow = saved.SelectedTrackPath is { } savedPath
                ? rows.FirstOrDefault(r => r.Track.Path == savedPath)
                : null;
            RestoreScrollOffsetForKey(newKey, saved.ScrollOffsetY);
        }
        else
        {
            MusicList.SelectedRow = null;
            RestoreScrollOffsetForKey(newKey, 0);
        }

        _currentViewKey = newKey;
    }

    // The page a view key is shown on. The Albums grid's key names the tiles
    // selected in it (LibraryBrowserViewModel.CurrentViewKey), so selecting
    // one - a Ctrl/Shift-click, a right-click, its "..." - changes the key
    // without leaving the page, and read as a view switch that put the grid
    // back at the top under the pointer. Every "album:" key is the one grid.
    private static string PageKey(string key) =>
        key.StartsWith("album:", StringComparison.Ordinal) ? "album:" : key;

    // CurrentViewKey's own prefix (see MainViewModel.CurrentViewKey) already
    // unambiguously says which control owns a given view's scroll - "album:"
    // for AlbumGrid, anything else (Songs/Artists/a Playlist) for MusicList -
    // so that string, not the ViewModel's current IsShowingAlbumGrid
    // (which can already reflect a *different*, newer selection by the time
    // this runs - see ApplyRows above), is what these two switch on.
    private double GetScrollOffsetYForKey(string key) =>
        key.StartsWith("album:", StringComparison.Ordinal) ? AlbumGrid.GetScrollOffsetY()
        : MusicList.GetScrollOffsetY();

    // Deferred a frame for the grid, not called inline like MusicList's
    // own SetScrollOffsetY - AlbumGridTiles (the grid's own ItemsSource) are reassigned a few lines *after* Rows in
    // MainViewModel.RebuildRowsAsync, so at the exact moment this runs
    // (triggered by Rows' own PropertyChanged) the grid's tiles are still the
    // *previous* view's - setting scroll now would just be clobbered once
    // the real ItemsSource change arrives and the grid rebuilds its rows.
    // Posting past that (both are plain, synchronous property assignments
    // within the same RebuildRowsAsync continuation) lets it land after.
    private void RestoreScrollOffsetForKey(string key, double offsetY)
    {
        if (key.StartsWith("album:", StringComparison.Ordinal))
            Dispatcher.UIThread.Post(() => AlbumGrid.SetScrollOffsetY(offsetY));
        else
            MusicList.SetScrollOffsetY(offsetY);
    }

    // MainWindow.Closing calls this alongside SaveWindowGeometry, capturing
    // whichever view is showing *right now* (not the last-switched-away-from
    // one _viewStates otherwise tracks) - the user may never have switched
    // views at all this session, in which case _currentViewKey already holds
    // the only view there ever was.
    //
    // Falls back to _viewModel.CurrentViewKey (computed live from
    // SelectedSidebarItem, always available) rather than bailing out when
    // _currentViewKey is still null - that field is only ever populated as a
    // side effect of ApplyRows actually having run once (a Rows-changed
    // event, i.e. the background scan's first result). Quitting before that
    // first result lands - a real, observed case, not just hypothetical -
    // used to save nothing at all here even though the sidebar selection and
    // window geometry were both perfectly well-known at that point.
    public void SaveCurrentViewState()
    {
        if (_viewModel == null)
            return;
        var key = _currentViewKey ?? _viewModel.CurrentViewKey;
        _viewModel.SaveLastView(GetScrollOffsetYForKey(key));
    }

    // ── Status bar ────────────────────────────────────────────────────────────

    // Up when a message arrives and for as long as the work goes on, then
    // down a second after it ends. A null message is the work ending, not
    // something to say, so what was last said stays up through that second -
    // which is also what keeps an operation that finishes in a blink from
    // being a flicker nobody can read.
    private void ShowStatusMessage(string? message)
    {
        if (message == null)
            return;

        _statusHideTimer?.Stop();
        StatusText.Text = message;
        StatusBar.Classes.Set("shown", true);
        UpdateSpinner();
    }

    private void HideStatusBarSoon()
    {
        if (_statusHideTimer == null)
        {
            _statusHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statusHideTimer.Tick += (_, _) => HideStatusBar();
        }
        _statusHideTimer.Stop();
        _statusHideTimer.Start();
    }

    private void HideStatusBar()
    {
        _statusHideTimer?.Stop();
        StatusBar.Classes.Set("shown", false);
        UpdateSpinner();
    }

    // ── Spinner ───────────────────────────────────────────────────────────────

    // Turning only while there is work going on and the bar is up to show it:
    // a spinner below the page's edge would still cost a frame every tick.
    private void UpdateSpinner()
    {
        if (_viewModel is { IsBusy: true } && StatusBar.Classes.Contains("shown"))
            StartSpinner();
        else
            StopSpinner();
    }

    private void StartSpinner()
    {
        if (_spin != null)
            return;

        var transform = new RotateTransform();
        _spinTransform = transform;
        SpinnerIcon.RenderTransformOrigin = RelativePoint.Center;
        SpinnerIcon.RenderTransform = transform;
        // Shared 60Hz clock rather than a timer of its own - see AnimationClock.
        _spin = AnimationClock.Current.Subscribe(
            elapsed => transform.Angle = elapsed.TotalSeconds * 360 % 360);
    }

    private void StopSpinner()
    {
        _spin?.Dispose();
        _spin = null;
        _spinTransform = null;
        
        if (SpinnerIcon != null)
            SpinnerIcon.RenderTransform = null;
    }

    // ── Songs dropped on the window ───────────────────────────────────────────

    // Song files, and folders, which may hold some - looking inside a folder
    // is left to the drop, not done on every move of the pointer.
    private static List<string> DroppedPaths(IDataTransfer data) =>
        data.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .OfType<string>()
            .Where(path => System.IO.Directory.Exists(path) || Importer.Importer.IsImportable(path))
            .ToList() ?? [];

    private void OnSongsDragOver(object? sender, DragEventArgs e)
    {
        var accepted = _viewModel is { AcceptsDroppedSongs: true } && DroppedPaths(e.DataTransfer).Count > 0;
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSongsDropped(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var paths = DroppedPaths(e.DataTransfer);
        if (_viewModel != null && paths.Count > 0)
            _viewModel.AddDroppedSongsAsync(paths).Forget(Logger, "Adding dropped songs");
    }

    // ── MusicListView event handlers ──────────────────────────────────────────

    // The row's own position is the queue position - see MainViewModel.PlayTrack's
    // queueIndex overload for why handing it over matters with duplicate entries.
    //
    // Not on the queue, which is not re-anchored from its own page: that page
    // is the queue, and playing a row on it is a jump along it.
    private void OnRowActivated(object? sender, TrackRowViewModel row)
    {
        if (_viewModel is not { } vm)
            return;
        if (vm.IsShowingQueue)
            vm.Queue.Play(row);
        else
            vm.PlayTrack(row.Track, vm.Rows.IndexOf(row));
    }

    // The download icon beside a song's title (see TrackDownloadButton) -
    // acts on that one row only, whatever the current selection is, same as
    // clicking any other per-row affordance.
    private void OnRowDownloadRequested(object? sender, TrackRowViewModel row)
    {
        if (_viewModel is { } vm)
            _ = vm.Downloads.DownloadRowAsync(row);
    }

    private void OnRowContextMenu(object? sender, TrackRowViewModel row)
    {
        // MusicList's Panel_ContextRequested already guarantees the selection is
        // either preserved (row was already selected) or collapsed to just this
        // row (it wasn't) before this fires, so SelectedTracks is always the
        // right set to act on here.
        if (_viewModel is not { } vm)
            return;

        var tracks = MusicList.SelectedTracks;
        // Remove from Queue only on the Queue page, and only when the
        // selection holds something other than the song playing.
        var groups = ActionMenus.ForSongs(
            vm.MenuFactsFor(tracks, LibraryRemovalDialog.Service),
            canRemoveFromQueue: vm.IsShowingQueue && MusicList.SelectedRows.Any(vm.Queue.CanRemove),
            canLocateFiles: PlatformShell.IsAvailable);
        PlatformContextMenu.Open(DesktopActionMenu.Build(groups, RunTrackListAction, tracks, vm), MusicList);
    }

    // The track list's song menu, over its selection - in the order the list
    // shows it rather than the order it was clicked in, for what queues.
    private async void RunTrackListAction(MenuAction action)
    {
        if (_viewModel is not { } vm)
            return;

        var tracks = MusicList.SelectedTracks;
        switch (action)
        {
            case MenuAction.PlayNext:
                vm.PlayNext(SelectedTracksInListOrder());
                break;
            case MenuAction.AddToQueue:
                vm.AddToQueue(SelectedTracksInListOrder());
                break;
            case MenuAction.RemoveFromQueue:
                vm.Queue.Remove(MusicList.SelectedRows);
                break;
            case MenuAction.GetInfo:
                OpenTrackInfo();
                break;
            case MenuAction.LocateFile:
                LocateFile();
                break;
            case MenuAction.Download:
                StartDownload(tracks);
                break;
            case MenuAction.DeleteLocalFiles:
                await LocalFileDeletionDialog.DeleteAsync(TopLevel.GetTopLevel(this), vm, tracks);
                break;
            case MenuAction.RemoveFromLibrary:
                await LibraryRemovalDialog.RemoveAsync(TopLevel.GetTopLevel(this), tracks);
                break;
        }
    }

    private void OnHeaderContextMenu(object? sender, EventArgs e)
        => PlatformContextMenu.Open(_columnMenu, MusicList);

    // The queue is in the order it plays in, so its headers sort nothing.
    private void OnSortRequested(object? sender, string columnId)
    {
        if (_viewModel?.IsShowingQueue == true)
            return;
        _viewModel?.SortByColumnCommand?.Execute(columnId);
    }

    // Right-click on a Playlist or Device row in the sidebar. SidebarList is a
    // stock ListBox (unlike MusicList's hand-rolled hit-testing), so the target
    // row is found by walking up from the routed event's Source to its
    // containing ListBoxItem.
    private void SidebarList_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Visual visual)
            return;
        if (_viewModel is not MainViewModel vm)
            return;

        var item = visual.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as SidebarItem;

        _sidebarItemMenu.Items.Clear();

        // Right-clicking one of several selected playlists acts on all of them;
        // right-clicking any other row acts on that row alone, leaving the
        // selection as it is - the Finder's rule.
        var selectedPlaylists = SelectedSidebarPlaylists();
        if (item is { Kind: SidebarItemKind.Playlist, Playlist: { } clicked }
            && selectedPlaylists.Count > 1
            && selectedPlaylists.Contains(clicked))
        {
            // Rename and Edit Rules are about one playlist, so they are not
            // offered here.
            var deleteAllItem = new MenuItem { Header = $"Delete {selectedPlaylists.Count} Playlists" }.WithIcon(MaterialIconKind.TrashCanOutline);
            deleteAllItem.Click += async (_, _) => await vm.DeletePlaylistsAsync(selectedPlaylists);
            _sidebarItemMenu.Items.Add(deleteAllItem);

            var smart = selectedPlaylists.Where(p => p.IsSmart).ToList();
            if (smart.Count > 0 && vm.SmartPlaylists != null)
            {
                _sidebarItemMenu.Items.Add(new Separator());

                var convertAllItem = new MenuItem
                {
                    Header = smart.Count == 1
                        ? "Convert Smart Playlist to Ordinary"
                        : $"Convert {smart.Count} Smart Playlists to Ordinary",
                }.WithIcon(MaterialIconKind.PlaylistEdit);
                convertAllItem.Click += async (_, _) =>
                {
                    foreach (var playlist in smart)
                        await vm.ConvertPlaylistToOrdinary(playlist);
                };
                _sidebarItemMenu.Items.Add(convertAllItem);
            }
        }
        else if (item is { Kind: SidebarItemKind.Playlist, Playlist: { } playlist })
        {
            // Reuses the same IsEditing/RenameBox flow CreatePlaylistWithTrack
            // already drops a freshly-created playlist into - see
            // RenameBox_Loaded/KeyDown/LostFocus and CommitRename below.
            var playNextItem = new MenuItem { Header = "Play Next" }.WithIcon(MaterialIconKind.PlaylistPlay);
            playNextItem.Click += (_, _) => vm.PlayNext(playlist.Tracks.ToList());
            _sidebarItemMenu.Items.Add(playNextItem);

            var addToQueueItem = new MenuItem { Header = "Add to Queue" }.WithIcon(MaterialIconKind.PlaylistMusic);
            addToQueueItem.Click += (_, _) => vm.AddToQueue(playlist.Tracks.ToList());
            _sidebarItemMenu.Items.Add(addToQueueItem);
            _sidebarItemMenu.Items.Add(new Separator());

            var renameItem = new MenuItem { Header = "Rename Playlist" }.WithIcon(MaterialIconKind.Pencil);
            renameItem.Click += (_, _) => BeginRename(item);
            _sidebarItemMenu.Items.Add(renameItem);

            var deleteItem = new MenuItem { Header = "Delete Playlist" }.WithIcon(MaterialIconKind.TrashCanOutline);
            deleteItem.Click += async (_, _) => await vm.DeletePlaylistAsync(playlist);
            _sidebarItemMenu.Items.Add(deleteItem);

            // Only on a smart playlist, and only when there is a refresher to
            // recompute what the editor saves - see MainViewModel.SmartPlaylists.
            if (playlist.IsSmart && vm.SmartPlaylists != null)
            {
                _sidebarItemMenu.Items.Add(new Separator());

                var editRulesItem = new MenuItem { Header = "Edit Rules…" }.WithIcon(MaterialIconKind.Tune);
                editRulesItem.Click += (_, _) => vm.EditSmartPlaylist(playlist);
                _sidebarItemMenu.Items.Add(editRulesItem);

                var convertItem = new MenuItem { Header = "Convert to Ordinary Playlist" }.WithIcon(MaterialIconKind.PlaylistEdit);
                convertItem.Click += async (_, _) => await vm.ConvertPlaylistToOrdinary(playlist);
                _sidebarItemMenu.Items.Add(convertItem);
            }
        }
        // A rename can only persist against a resolved fingerprint (see
        // DeviceNicknameStore, keyed by fingerprint rather than the mDNS
        // instance name) - not yet available in the brief window before a
        // freshly-discovered device's /info handshake resolves it.
        else if (item is { Kind: SidebarItemKind.Device, Device.Fingerprint.Length: > 0 })
        {
            var renameItem = new MenuItem { Header = "Rename Device" }.WithIcon(MaterialIconKind.Pencil);
            renameItem.Click += (_, _) => BeginRename(item);
            _sidebarItemMenu.Items.Add(renameItem);
        }
        // The "Playlists" header, the empty space below the rows, or any row
        // with no menu of its own. New Smart Playlist… needs a home reachable
        // on every platform, and the app menu is not one: MainWindow's
        // NativeMenu is macOS-only (see its own comment), so on Windows and
        // Linux this is the only way to reach it.
        else
        {
            var newPlaylistItem = new MenuItem { Header = "New Playlist" }.WithIcon(MaterialIconKind.PlaylistPlus);
            newPlaylistItem.Click += async (_, _) => await vm.CreatePlaylistWithTrack(null);
            _sidebarItemMenu.Items.Add(newPlaylistItem);

            if (vm.SmartPlaylists != null)
            {
                var newSmartItem = new MenuItem { Header = "New Smart Playlist…" }.WithIcon(MaterialIconKind.PlaylistStar);
                newSmartItem.Click += (_, _) => vm.NewSmartPlaylist();
                _sidebarItemMenu.Items.Add(newSmartItem);
            }
        }

        PlatformContextMenu.Open(_sidebarItemMenu, SidebarList);
        e.Handled = true;
    }

    // ── Drag a track onto a sidebar playlist ────────────────────────────────────
    // See MusicListView's TrackDragMoved/TrackDragEnded for the source side and why
    // this is built on plain pointer capture rather than Avalonia's native
    // DragDrop (a real OS-level drag on macOS, which leaked a missed drop into
    // Music.app instead of harmlessly doing nothing). Only active outside a
    // playlist's own view (AllowReorder owns the drag gesture there instead, for
    // reordering within it).

    // A hit on a specific playlist row (PlaylistItem set) adds to that playlist;
    // a hit anywhere else in the Playlists section (CreateNew) - including its
    // empty tail, or the whole section when there are no playlists yet -
    // creates a new one instead. A hit on the Queue row (PlaylistItem is that
    // row, IsQueue set) adds to the end of the queue, as Add to Queue does.
    // See HitTestSidebarDrop.
    private readonly record struct SidebarDropTarget(SidebarItem? PlaylistItem, bool CreateNew, bool IsQueue = false);

    private void OnTrackDragMoved(object? sender, (IReadOnlyList<Track> tracks, Point position) e)
    {
        ShowDragGhost(MusicList, e.position, DragGhostLabel(e.tracks));
        SetSidebarDropHighlight(HitTestSidebarDrop(MusicList, e.position));
    }

    private void OnTrackDragEnded(object? sender, (IReadOnlyList<Track> tracks, Point position) e)
    {
        var target = HitTestSidebarDrop(MusicList, e.position);
        ResetDragVisuals();

        // In the order the list shows them, as the menu's Add to Queue takes
        // them - the drag carries them in the order they were clicked.
        if (target?.IsQueue == true)
            _viewModel?.AddToQueue(SelectedTracksInListOrder());
        else if (target?.PlaylistItem?.Playlist is { } playlist)
            _ = _viewModel?.AddTracksToPlaylist(e.tracks, playlist);
        else if (target?.CreateNew == true)
            _ = _viewModel?.CreatePlaylistWithTracks(e.tracks);
    }

    private static string DragGhostLabel(IReadOnlyList<Track> tracks) =>
        tracks.Count == 1 ? (tracks[0].Title ?? "Untitled") : $"{tracks.Count} tracks";

    // ── Drag an album/artist name (SubList) onto a sidebar playlist ─────────────
    // Same gesture as OnTrackDragMoved/OnTrackDragEnded above, but SubList is a
    // stock ListBox rather than MusicListView's hand-rolled panel, so there's no
    // control-owned event to subscribe to - the pointer sequence is handled
    // directly here instead.

    private void SubList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_subListGesture is not { } gesture)
            return;
        if (!e.GetCurrentPoint(SubList).Properties.IsLeftButtonPressed)
            return;
        var container = (SubList.InputHitTest(e.GetPosition(SubList)) as Visual)
            ?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (container?.DataContext is not string item)
            return;

        // SubList's selection is fully owned by the gesture rather than left
        // to ListBoxItem's native SelectionMode="Multiple" click handling,
        // which doesn't give the plain-click-collapses-everything-else /
        // Ctrl-toggle / Shift-range semantics this needs - mirrors
        // MusicListView's own hand-rolled Panel_PointerPressed selection logic.
        gesture.Press(
            item,
            e.GetPosition(SubList),
            e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            e.KeyModifiers.HasFlag(PlatformShortcuts.Primary));

        container.Focus();
        e.Handled = true;

        e.Pointer.Capture(SubList);
    }

    private void SubList_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_subListGesture is not { } gesture)
            return;

        var pt = e.GetPosition(SubList);
        if (!gesture.Move(pt))
            return;

        ShowDragGhost(SubList, pt, DragGhostLabel(gesture.DragItems!));
        SetSidebarDropHighlight(HitTestSidebarDrop(SubList, pt));
    }

    private void SubList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_subListGesture is { IsDragging: true, DragItems: { } items })
            CompleteNameDrop(SubList, e.GetPosition(SubList), items);

        e.Pointer.Capture(null);
        EndSubListDrag();
    }

    private void SubList_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => EndSubListDrag();

    private static string DragGhostLabel(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : $"{items.Count} items";

    private void EndSubListDrag()
    {
        _subListGesture?.End();
        ResetDragVisuals();
    }

    // The drop half of the name-drag gesture, shared by SubList and both
    // album grids - identical for all three, since what's dropped is always a
    // set of album/artist names resolved to tracks the same way.
    private void CompleteNameDrop(Visual source, Point position, IReadOnlyList<string> items)
    {
        var tracks = _viewModel?.GetTracksForSubListItems(items) ?? Enumerable.Empty<Track>();
        CompleteTracksDrop(HitTestSidebarDrop(source, position), tracks.ToList());
    }

    private void CompleteTracksDrop(SidebarDropTarget? target, List<Track> tracks)
    {
        if (target?.IsQueue == true)
            _viewModel?.AddToQueue(tracks);
        else if (target?.PlaylistItem?.Playlist is { } playlist)
            _ = _viewModel?.AddTracksToPlaylist(tracks, playlist);
        else if (target?.CreateNew == true)
            _ = _viewModel?.CreatePlaylistWithTracks(tracks);
    }

    private void ShowDragGhost(Visual source, Point sourcePosition, string text)
    {
        if (source.TranslatePoint(sourcePosition, ContentGrid) is { } ghostPos)
        {
            DragGhost.Margin = new Thickness(ghostPos.X + 14, ghostPos.Y + 14, 0, 0);
            DragGhost.IsVisible = true;
            DragGhostText.Text = text;
        }
    }

    // sourcePosition is in source's own local coordinate space (e.g.
    // MusicListView's TrackDragMoved/TrackDragEnded, or SubList's pointer
    // events) - translated here into SidebarList's space. A specific playlist
    // row wins if the pointer is directly over one; otherwise falls back to
    // GetPlaylistsDropBand to see if the pointer is still somewhere within the
    // Playlists section.
    private SidebarDropTarget? HitTestSidebarDrop(Visual source, Point sourcePosition)
    {
        if (source.TranslatePoint(sourcePosition, SidebarList) is not { } sidebarPos)
            return null;
        if (!new Rect(SidebarList.Bounds.Size).Contains(sidebarPos))
            return null;

        var hit = (SidebarList.InputHitTest(sidebarPos) as Visual)
            ?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as SidebarItem;

        // The queue takes songs and albums the way Add to Queue does - except
        // its own rows, dragged from its own page: dropped back on it they
        // would only be queued a second time.
        if (hit is { Kind: SidebarItemKind.Queue })
        {
            return source == MusicList && _viewModel?.IsShowingQueue == true
                ? new SidebarDropTarget(null, false)
                : new SidebarDropTarget(hit, false, IsQueue: true);
        }

        if (hit is { Kind: SidebarItemKind.Playlist } item)
        {
            // A smart playlist holds what its rules say and nothing else, so
            // this is a refusal, not a miss: neither the row's highlight nor
            // the create-a-new-playlist band below should light up while the
            // pointer is over it.
            return item.Playlist is { IsSmart: true }
                ? new SidebarDropTarget(null, false)
                : new SidebarDropTarget(item, false);
        }

        if (GetPlaylistsDropBand() is { } band && sidebarPos.Y >= band.Top && sidebarPos.Y < band.Bottom)
            return new SidebarDropTarget(null, true);

        return null;
    }

    // The "drop here to create a new playlist" zone spans everything below the
    // Library section (Songs/Albums/Artists) and above Devices/Server (if
    // either is present - see MainViewModel.DeviceSectionHeaderName) - so a
    // drop that misses a specific playlist row still creates a playlist rather
    // than silently doing nothing, and this works even with zero playlists (no
    // "Playlists" header exists yet in that case). Computed from realized
    // container bounds rather than a real stretchy layout element, since
    // SidebarList's items are sized to content and don't grow to fill its
    // leftover height.
    private Rect? GetPlaylistsDropBand()
    {
        if (_viewModel is not { } vm)
            return null;

        var items = vm.SidebarItems;
        var libraryEndIndex = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Kind is SidebarItemKind.Songs or SidebarItemKind.Albums or SidebarItemKind.Artists)
                libraryEndIndex = i;
        }
        if (libraryEndIndex < 0)
            return null;

        var devicesHeaderIndex = -1;
        for (var i = libraryEndIndex + 1; i < items.Count; i++)
        {
            if (items[i] is { Kind: SidebarItemKind.Header, Name: "Devices" or "Server" })
            {
                devicesHeaderIndex = i;
                break;
            }
        }

        var top = SidebarList.ContainerFromIndex(libraryEndIndex) is Control lastLibraryRow
            ? lastLibraryRow.Bounds.Bottom
            : 0;
        var bottom = devicesHeaderIndex >= 0 && SidebarList.ContainerFromIndex(devicesHeaderIndex) is Control devicesHeader
            ? devicesHeader.Bounds.Top
            : SidebarList.Bounds.Height;

        return bottom > top ? new Rect(0, top, SidebarList.Bounds.Width, bottom - top) : null;
    }

    private void SetDropTargetHighlight(SidebarItem? item)
    {
        if (_dropTargetPlaylistItem == item)
            return;
        if (_dropTargetPlaylistItem != null)
            _dropTargetPlaylistItem.IsDropTarget = false;
        _dropTargetPlaylistItem = item;
        if (_dropTargetPlaylistItem != null)
            _dropTargetPlaylistItem.IsDropTarget = true;
    }

    private void SetSidebarDropHighlight(SidebarDropTarget? target)
    {
        SetDropTargetHighlight(target?.PlaylistItem);

        if (target?.CreateNew == true && GetPlaylistsDropBand() is { } band)
        {
            NewPlaylistDropZone.Margin = new Thickness(4, band.Top + 1, 4, 0);
            NewPlaylistDropZone.Height = Math.Max(0, band.Height - 2);
            NewPlaylistDropZone.IsVisible = true;
        }
        else
        {
            NewPlaylistDropZone.IsVisible = false;
        }
    }

    private void ResetDragVisuals()
    {
        DragGhost.IsVisible = false;
        NewPlaylistDropZone.IsVisible = false;
        SetDropTargetHighlight(null);
    }

    // ── Context menus ─────────────────────────────────────────────────────────

    private void BuildColumnMenu()
    {
        if (_viewModel == null)
            return;
        var columnManager = MusicList.ColumnManager;

        _columnMenu = new ContextMenu();
        foreach (var col in columnManager.Columns)
        {
            var col1 = col; // capture
            var item = new MenuItem { Header = col.Header };
            SetCheckIcon(item, col.IsVisible);
            item.Click += (_, _) =>
            {
                col1.IsVisible = !col1.IsVisible;
                SetCheckIcon(item, col1.IsVisible);
            };
            _columnMenu.Items.Add(item);
        }
    }

    private List<Track> SelectedTracksInListOrder()
    {
        // Rows rather than tracks, so a playlist holding one song twice
        // queues the copy that was selected and not both.
        var selected = MusicList.SelectedRows.ToHashSet();
        return ShownRows.Where(selected.Contains).Select(r => r.Track).ToList();
    }

    // The album menu for a right-clicked album tile - see
    // AlbumGrid_ContextRequested. Built fresh per request. Play and Shuffle
    // play just the tile clicked; everything else acts on the full current
    // album selection, via tracks.
    //
    // getInfo is what Get Info opens: the grid's album selection unless the
    // caller says otherwise (a Home tile, which has none).
    private ContextMenu BuildAlbumContextMenu(IReadOnlyList<Track> tracks, AlbumTileViewModel clickedTile, Action? getInfo = null)
    {
        var vm = _viewModel!;
        var groups = ActionMenus.ForAlbum(
            vm.MenuFactsFor(tracks, LibraryRemovalDialog.Service),
            isContinuing: vm.Home.IsContinuing(clickedTile),
            isRecentlyPlayed: vm.Home.IsOnRecentlyPlayed(clickedTile));

        return DesktopActionMenu.Build(groups, async action =>
        {
            switch (action)
            {
                case MenuAction.Play:
                    vm.PlayAlbum(clickedTile);
                    break;
                case MenuAction.Shuffle:
                    vm.PlayTracks(clickedTile.Tracks, shuffle: true);
                    break;
                case MenuAction.PlayNext:
                    vm.PlayNext(tracks);
                    break;
                case MenuAction.AddToQueue:
                    vm.AddToQueue(tracks);
                    break;
                case MenuAction.GetInfo:
                    if (getInfo != null)
                        getInfo();
                    else
                        OpenTrackInfoForSelectedAlbums();
                    break;
                case MenuAction.Download:
                    StartDownload(tracks);
                    break;
                case MenuAction.ForgetProgress:
                    vm.Home.Forget(clickedTile);
                    break;
                case MenuAction.HideFromRecentlyPlayed:
                    vm.Home.HideFromRecentlyPlayed(clickedTile);
                    break;
                case MenuAction.DeleteLocalFiles:
                    await LocalFileDeletionDialog.DeleteAsync(TopLevel.GetTopLevel(this), vm, tracks);
                    break;
                case MenuAction.RemoveFromLibrary:
                    await LibraryRemovalDialog.RemoveAsync(TopLevel.GetTopLevel(this), tracks);
                    break;
            }
        }, tracks, vm);
    }

    private void StartDownload(IReadOnlyList<Track> tracks)
    {
        if (_viewModel is { } vm)
            _ = vm.Downloads.DownloadAllAsync(tracks.Where(vm.Availability.IsDownloadable).ToList());
    }

    private static void SetCheckIcon(MenuItem item, bool visible)
    {
        item.Icon = visible
            ? new MaterialIcon { Kind = MaterialIconKind.Check, Width = 14, Height = 14 }
            : null;
    }

    // ── Keyboard (tunnel to catch keys before MusicListView) ─────────────────

    private void MusicList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _viewModel?.PlayOrPauseFromCurrentView();
            e.Handled = true;
        }
        // Enter is handled inside MusicListView (fires RowActivated)
    }

    private void MainView_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Still needed here for Windows/Linux, which have no native app menu
        // at all (App.axaml's NativeMenu.Menu is macOS-only - see its own
        // comment). On macOS specifically this case is unreachable dead code,
        // not incorrect: the app menu's own Settings… item (Gesture="Cmd+OemComma")
        // is resolved by the OS before a Cmd+, key event ever reaches
        // Avalonia's input pipeline at all - that resolution happens whether
        // or not this case exists, which is exactly why leaving it in without
        // also giving the menu item its own Gesture silently broke the
        // shortcut on macOS rather than merely duplicating it.
        if (e.Key == Key.OemComma && e.KeyModifiers == PlatformShortcuts.Primary)
        {
            _viewModel?.OpenSettingsCommand?.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.L && e.KeyModifiers == PlatformShortcuts.Primary)
        {
            _ = _viewModel?.GoToCurrentlyPlayingTrackAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.J && e.KeyModifiers == PlatformShortcuts.Primary)
        {
            _viewModel?.OpenColumnSelectorCommand?.Execute(null);
            e.Handled = true;
        }
        // Space play/pause, reachable from anywhere in MainView - MusicList's
        // own tunnel handler (MusicList_KeyDown) only fires while focus is
        // somewhere inside MusicList, so this is the fallback for focus on
        // the sidebar, the Albums grid, the Artists sub-list, etc.
        // (same reasoning as the Cmd/Ctrl+I fallback below). Skipped while a
        // TextBox has focus (search box, playlist rename) so Space still
        // types a literal space there instead of toggling playback.
        else if (e.Key == Key.Space &&
                 (e.Source is not Visual sourceVisual || sourceVisual.FindAncestorOfType<TextBox>(includeSelf: true) is null))
        {
            _viewModel?.PlayOrPauseFromCurrentView();
            e.Handled = true;
        }
        // The menu bar's Get Info, Filter and volume shortcuts. On macOS the
        // menu takes these keys before Avalonia sees them, as it does Cmd+,
        // above, and runs the same methods; this is Windows and Linux.
        else if (e.Key == Key.I && e.KeyModifiers == PlatformShortcuts.Primary)
        {
            GetInfo();
            e.Handled = true;
        }
        else if (e.Key == Key.K && e.KeyModifiers == PlatformShortcuts.Primary)
        {
            FocusFilter();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down && e.KeyModifiers == PlatformShortcuts.Primary)
        {
            ChangeVolume(e.Key == Key.Up ? VolumeStep : -VolumeStep);
            e.Handled = true;
        }
        else if (e.KeyModifiers == PlatformShortcuts.Primary && SectionKeys.FirstOrDefault(s => s.Key == e.Key) is { Kind: not SidebarItemKind.Header } section)
        {
            ShowSection(section.Kind);
            e.Handled = true;
        }
    }

    // ── Menu bar ──────────────────────────────────────────────────────────────
    // What MainWindow's menus run that only the view can: they act on what
    // is on screen and selected in it, which the view model does not hold.

    // Get Info, from anywhere: the songs selected in the list, or the albums
    // selected in the grid, or - with nothing selected where focus is, Home
    // and the Artists page included - the song playing.
    public void GetInfo()
    {
        if (_viewModel is not { } vm)
            return;

        if (vm.IsShowingAlbumGrid && OpenTrackInfoForSelectedAlbums())
            return;
        if (ShowsTrackList(vm) && MusicList.SelectedTracks.Count > 0)
        {
            OpenTrackInfo();
            return;
        }
        if (vm.NowPlaying.CurrentlyPlayingTrack is { } playing)
            OpenTrackInfo(new[] { playing });
    }

    // The songs the Song menu acts on: those selected in the list on screen,
    // or under the albums selected in the grid.
    public IReadOnlyList<Track> SelectedSongs()
    {
        if (_viewModel is not { } vm)
            return [];

        if (vm.IsShowingAlbumGrid)
        {
            var songs = AlbumGrid.GetExpandedRowSelectedTracks();
            return songs.Count > 0 ? songs : vm.GetTracksForSubListItems(vm.SelectedSubItems).ToList();
        }
        return ShowsTrackList(vm) ? SelectedTracksInListOrder() : [];
    }

    // The first of them with a file here: a song streamed from the server has
    // none to show.
    public void LocateSelectedFile() =>
        PlatformShell.Reveal(LocalFileDeletion.LocalFiles(SelectedSongs()).FirstOrDefault()?.Path);

    public void DownloadSelectedSongs() => StartDownload(SelectedSongs());

    // The track list is the page - not Home, the Artists page or a server's,
    // where a selection it kept from another page would be acted on unseen.
    private static bool ShowsTrackList(MainViewModel vm) =>
        !vm.IsShowingHome && !vm.IsShowingArtistPage && !vm.IsShowingDeviceDetail && !vm.IsShowingServerSettings;

    // The sidebar's Library rows, in its order, and the Cmd/Ctrl+number
    // that shows each - MainWindow's View menu lists them.
    public static readonly (SidebarItemKind Kind, Key Key)[] SectionKeys =
    [
        (SidebarItemKind.Home, Key.D1),
        (SidebarItemKind.Queue, Key.D2),
        (SidebarItemKind.History, Key.D3),
        (SidebarItemKind.Songs, Key.D4),
        (SidebarItemKind.Albums, Key.D5),
        (SidebarItemKind.Artists, Key.D6),
    ];

    public void ShowSection(SidebarItemKind kind)
    {
        if (_viewModel is { } vm && vm.SidebarItems.FirstOrDefault(i => i.Kind == kind) is { } row)
            vm.SelectedSidebarItem = row;
    }

    // Cmd/Ctrl+K: the page header's filter box, where the page has one.
    public void FocusFilter()
    {
        var filter = this.GetVisualDescendants().OfType<UserControls.FilterControl>().FirstOrDefault(f => f.IsEffectivelyVisible);
        filter?.FocusBox();
    }

    private const int VolumeStep = 10;

    public void ChangeVolume(int by)
    {
        if (_viewModel?.Volume is { } volume)
            volume.Volume = Math.Clamp(volume.Volume + by, 0, 100);
    }

    private void OnNavigateToTrackRequested(object? sender, Track track) => MusicList.ScrollToTrack(track);

    private void OnHomeAlbumOpened(object? sender, AlbumTileViewModel album) =>
        _viewModel?.ShowAlbumAsync(album).Forget(Logger, "Opening an album from Home");

    // Posted for the same reason RestoreScrollOffsetForKey is: the grid's
    // tiles arrive just after the rows that raised this.
    private void OnAlbumRevealRequested(object? sender, AlbumTileKey key) =>
        Dispatcher.UIThread.Post(() => AlbumGrid.ScrollToTile(key));

    private void OnSettingsRequested(object? sender, EventArgs e) => OpenSettingsWindow();

    private void OnColumnSelectorRequested(object? sender, EventArgs e) => OpenColumnSelectorWindow();

    private void OnLogWindowRequested(object? sender, EventArgs e) => OpenLogWindow();

    private void OnEqualizerWindowRequested(object? sender, EventArgs e) => OpenEqualizerWindow();

    // Raised by MainViewModel (forwarding PlaylistSyncService.ConflictDetected)
    // when the same playlist changed on both this device and a peer since they
    // last agreed - see SYNC-PLAN.md Phase 2. The dialog's result is fed back into
    // e.Resolution, which unblocks that one playlist's merge on the sync-session
    // background task; nothing else about the sync waits on the dialog closing.
    private async void OnPlaylistConflictRequested(object? sender, PlaylistConflictEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            e.Resolution.TrySetResult(PlaylistConflictChoice.KeepLocal);
            return;
        }

        var choice = await new PlaylistConflictWindow(e).ShowDialog<PlaylistConflictChoice>(owner);
        e.Resolution.TrySetResult(choice);
    }

    private async void OnDeletePlaylistConfirmationRequested(object? sender, DeletePlaylistConfirmationEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            e.Confirmed.TrySetResult(true);
            return;
        }

        var (title, message) = e.Playlists.Count == 1
            ? ("Delete Playlist?", $"\"{e.Playlists[0].Name}\" will be permanently deleted. This cannot be undone.")
            : ($"Delete {e.Playlists.Count} Playlists?", $"{e.Playlists.Count} playlists will be permanently deleted. This cannot be undone.");
        var confirmed = await ConfirmDialogWindow.ShowAsync(owner, title, message, "Delete");
        e.Confirmed.TrySetResult(confirmed);
    }

    private void OpenSettingsWindow()
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            if (_viewModel != null)
                new SettingsWindow(_viewModel).ShowDialog(owner);
            return;
        }

        // No Window to own a dialog - this is the browser (Avalonia.Browser is
        // a single-view lifetime), where Settings is a place rather than a
        // dialog. Cmd/Ctrl+, therefore navigates to it, the same row the
        // sidebar offers; the page itself is built by
        // UpdateServerSettingsPage below.
        SelectServerSettingsPage();
    }

    // Selects the sidebar's Server Settings row, if this host has one. Public
    // because App.axaml.cs calls it at startup when the page was opened with
    // page=settings in its URL fragment - the desktop client's "Server
    // Settings..." button, which wants the reader to land on that page and not
    // merely to be able to find it.
    public void SelectServerSettingsPage()
    {
        if (_viewModel?.SidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.ServerSettings) is { } row)
            _viewModel.SelectedSidebarItem = row;
    }

    private void OpenColumnSelectorWindow()
    {
        if (_viewModel == null)
            return;
        var columnSelectorWindow = new ColumnSelectorWindow(MusicList.ColumnManager, _viewModel);
        if (TopLevel.GetTopLevel(this) is Window owner)
            columnSelectorWindow.ShowDialog(owner);
        else
            columnSelectorWindow.Show();
    }

    // Non-modal (.Show(), no owner) unlike Settings/Column Selector's
    // ShowDialog - this window's whole point is to keep showing live log
    // activity while the user keeps using the rest of the app.
    private void OpenLogWindow()
    {
        if (_viewModel?.Log is not { } logViewModel)
            return;
        var logWindow = new LogWindow(logViewModel);
        logWindow.Show();
    }

    // Modal, unlike the EQ and log windows below: the playlist is not yet what
    // the rules say until OK is pressed, so leaving it open beside a sidebar
    // showing the old contents would be showing two answers at once.
    private async void OnSmartPlaylistEditorRequested(object? sender, SmartPlaylistEditorEventArgs e)
    {
        if (_viewModel is not MainViewModel vm || vm.SmartPlaylists is not { } refresher)
            return;

        var editor = new SmartPlaylistEditorWindow(
            new SmartPlaylistEditorViewModel(e.Playlist, vm.Library, refresher, e.IsNew));

        if (TopLevel.GetTopLevel(this) is Window owner)
            await editor.ShowDialog(owner);
        else
            editor.Show();

        // Both outcomes move the sidebar: a save can rename the playlist and
        // change its icon, and a cancel on a new one deletes the row outright.
        vm.Playlists.RefreshSidebarItems();
    }

    // Non-modal, same reasoning as OpenLogWindow - the EQ should stay open
    // and live-adjustable while music keeps playing.
    private void OpenEqualizerWindow()
    {
        if (_viewModel == null)
            return;
        var equalizerWindow = new EqualizerWindow(_viewModel.Equalizer);
        equalizerWindow.Show();
    }

    // The page's orders (BrowseSort), the one in use ticked with an arrow for
    // which way it runs - picked again, it turns round. The Artists page has
    // two lists to put in order, under a heading each.
    private void SortButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } vm)
            return;

        var menu = new ContextMenu();
        void AddSection(string? header, BrowseSortScreen screen)
        {
            if (header != null)
            {
                if (menu.Items.Count > 0)
                    menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = header, IsEnabled = false });
            }

            var (current, ascending) = vm.SortOf(screen);
            foreach (var order in BrowseSort.SortsFor(screen))
            {
                var item = new MenuItem { Header = BrowseSort.LabelFor(order) };
                if (order == current)
                {
                    item.Icon = new MaterialIcon
                    {
                        Kind = ascending ? MaterialIconKind.ArrowUp : MaterialIconKind.ArrowDown,
                        Width = 14,
                        Height = 14,
                    };
                }
                var picked = order;
                item.Click += (_, _) => vm.ChooseSort(screen, picked);
                menu.Items.Add(item);
            }
        }

        if (vm.IsShowingArtistPage)
        {
            AddSection("Artists", BrowseSortScreen.Artists);
            AddSection("Albums", BrowseSortScreen.ArtistAlbums);
        }
        else
        {
            AddSection(null, BrowseSortScreen.Albums);
        }

        PlatformContextMenu.Open(menu, SortButton);
    }

    // ── Track actions ─────────────────────────────────────────────────────────

    private void OpenTrackInfo()
    {
        if (_viewModel is not MainViewModel vm)
            return;

        TrackInfoWindow infoWindow;
        var selected = MusicList.SelectedTracks;
        if (selected.Count > 1)
        {
            // Batch mode: edit the whole multi-selection together, no Prev/Next.
            infoWindow = new TrackInfoWindow(selected, vm) { ShowInTaskbar = false };
        }
        else
        {
            if (MusicList.SelectedTrack is not Track track)
                return;
            // Prev/Next browse the rows the list is drawing, which on the
            // Queue page are the queue's and not the browser's - and by row,
            // since the queue can hold one song twice. A track the rows do
            // not hold gets a window of its own rather than someone else's.
            var rows   = ShownRows;
            var tracks = rows.Select(r => r.Track).ToList();
            var index  = MusicList.SelectedRows is [var row] ? rows.ToList().IndexOf(row) : -1;
            if (index < 0)
                index = tracks.IndexOf(track);
            if (index < 0)
            {
                tracks = [track];
                index  = 0;
            }
            infoWindow = new TrackInfoWindow(tracks, index, vm) { ShowInTaskbar = false };
            infoWindow.TrackNavigated += (_, t) => MusicList.SelectedTrack = t;
        }

        if (TopLevel.GetTopLevel(this) is Window owner)
            infoWindow.Show(owner);
        else
            infoWindow.Show();
    }

    // Cmd/Ctrl+I on Albums (see MainView_PreviewKeyDown). Which tracks that
    // acts on is AlbumTrackInfoSelection.Resolve's decision - see its own doc
    // comment for the precedence rules; all that's left here is reading the
    // grid's expanded-row selection and opening the window.
    private bool OpenTrackInfoForSelectedAlbums()
    {
        if (_viewModel is not MainViewModel vm)
            return false;

        var songSelection = AlbumGrid.GetExpandedRowSelectedTracks();

        var target = AlbumTrackInfoSelection.Resolve(
            songSelection,
            vm.ExpandedAlbumTracks,
            vm.SelectedSubItems,
            vm.ExpandedAlbumName,
            vm.GetTracksForSubListItems);
        if (target.IsEmpty)
            return false;

        var infoWindow = target.FocusIndex is { } index
            ? new TrackInfoWindow(target.Tracks, index, vm) { ShowInTaskbar = false }
            : new TrackInfoWindow(target.Tracks, vm) { ShowInTaskbar = false };

        if (TopLevel.GetTopLevel(this) is Window owner)
            infoWindow.Show(owner);
        else
            infoWindow.Show();
        return true;
    }

    // Get Info over a given set of songs - a Home tile's album.
    private void OpenTrackInfo(IReadOnlyList<Track> tracks)
    {
        if (_viewModel is not MainViewModel vm || tracks.Count == 0)
            return;

        var infoWindow = new TrackInfoWindow(tracks, vm) { ShowInTaskbar = false };
        if (TopLevel.GetTopLevel(this) is Window owner)
            infoWindow.Show(owner);
        else
            infoWindow.Show();
    }

    private void LocateFile() =>
        PlatformShell.Reveal(LocalFileDeletion.LocalFiles(SelectedTracksInListOrder()).FirstOrDefault()?.Path);

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private SidebarItem? _lastSelectableSidebarItem;

    // Set while SidebarList_SelectionChanged corrects the selection itself, so
    // the changes it makes are not judged again half-applied.
    private bool _settlingSidebarSelection;

    // Multiple selection is on for playlists only - see SidebarSelection.
    private void SidebarList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settlingSidebarSelection || sender is not ListBox { SelectedItems: { } selectedItems } list)
            return;

        var selected = selectedItems.OfType<SidebarItem>().ToList();
        var settled = SidebarSelection.Settle(selected, e.AddedItems.OfType<SidebarItem>().ToList(), _lastSelectableSidebarItem);
        if (settled is { Count: > 0 })
        {
            _settlingSidebarSelection = true;
            try
            {
                // One row: through SelectedItem, which replaces the selection
                // in one change - removing the others one at a time would walk
                // SelectedSidebarItem, and the track list, through each of them.
                if (settled.Count == 1)
                    list.SelectedItem = settled[0];
                else
                {
                    foreach (var extra in selected.Except(settled))
                        selectedItems.Remove(extra);
                }
            }
            finally
            {
                _settlingSidebarSelection = false;
            }
        }

        if (selectedItems.Count == 1 && list.SelectedItem is SidebarItem { IsHeader: false } item)
            _lastSelectableSidebarItem = item;

        // So the Playlist menu's Delete acts on the whole selection too.
        _viewModel?.SetSelectedPlaylists(SelectedSidebarPlaylists());
    }

    // The playlists selected together in the sidebar, in sidebar order.
    private List<Playlist> SelectedSidebarPlaylists()
    {
        var selected = SidebarList.SelectedItems?.OfType<SidebarItem>().ToHashSet() ?? new HashSet<SidebarItem>();
        return _viewModel?.SidebarItems
            .Where(selected.Contains)
            .Select(i => i.Playlist)
            .OfType<Playlist>()
            .ToList() ?? new List<Playlist>();
    }

    // ── Sidebar rename (new playlist) ────────────────────────────────────────

    private void RenameBox_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb)
            return;
        tb.Focus();
        tb.SelectAll();
    }

    // Entry point for renaming an *existing* playlist (the sidebar's own
    // context-menu, and the "Playlist > Rename Playlist" main-menu command below).
    // CreatePlaylistWithTrack's brand-new SidebarItem gets a freshly realized
    // container, so its TextBox's Loaded event fires and RenameBox_Loaded's
    // Focus() above just works. An existing item's container was realized long
    // ago, though - flipping IsEditing only flips that already-realized
    // TextBox's IsVisible, which does not raise Loaded again, so nothing ever
    // focused it (every downstream symptom - arrow keys reaching SidebarList,
    // Enter not confirming - traced back to this). Posted a frame so IsVisible
    // has actually applied before Focus() is attempted.
    private void BeginRename(SidebarItem item)
    {
        item.IsEditing = true;

        var index = _viewModel?.SidebarItems.IndexOf(item) ?? -1;
        if (index < 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (SidebarList.ContainerFromIndex(index) is Control container &&
                container.FindDescendantOfType<TextBox>() is { } tb)
            {
                tb.Focus();
                tb.SelectAll();
            }
        });
    }

    // "Playlist > Rename Playlist" main-menu command (see MainWindow.axaml and
    // MainViewModel.RenamePlaylistRequested) - operates on whichever playlist is
    // currently selected, since unlike the sidebar's context menu there is no
    // specific right-clicked row to go on.
    private void OnRenamePlaylistRequested(object? sender, EventArgs e)
    {
        if (_viewModel?.SelectedSidebarItem is { Kind: SidebarItemKind.Playlist } item)
            BeginRename(item);
    }

    private void RenameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: SidebarItem item })
            return;

        switch (e.Key)
        {
            case Key.Enter:
            case Key.Escape:
                // Handled must be set before the (async) commit, not after -
                // otherwise, since this is fire-and-forget, the KeyDown event has
                // already finished bubbling with Handled still false by the time
                // CommitRename's awaited save completes, letting SidebarList's own
                // KeyDown handling see an apparently-unhandled Enter/Escape too.
                e.Handled = true;
                _ = CommitRename(item);
                break;
            case Key.Up:
            case Key.Down:
            case Key.Left:
            case Key.Right:
                // Keep arrow keys scoped to the rename box - otherwise SidebarList's
                // own arrow-key handling moves SelectedItem (and, since it drops
                // focus off this TextBox, ends the rename) while this one is still
                // mid-rename. Left/Right normally never reach here at all - the
                // TextBox's own caret movement already marks them handled - except
                // right at the start/end of the text, where there is no caret move
                // left to make and the event falls through unhandled.
                e.Handled = true;
                break;
        }
    }

    private async void RenameBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SidebarItem item })
            await CommitRename(item);
    }

    // LostFocus alone isn't enough to end a rename: not every control a click can
    // land on actually takes keyboard focus (MusicListView's hand-rolled row panel
    // only calls Focus() when the click hits an actual row - see its
    // Panel_PointerPressed), so a click elsewhere can leave the textbox focused
    // and the item stuck in edit mode. Tunnelled so it commits before the click's
    // own target handles it.
    private void MainView_PreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel?.SidebarItems.FirstOrDefault(i => i.IsEditing) is not { } editing)
            return;
        if (e.Source is Visual visual && visual.FindAncestorOfType<TextBox>(includeSelf: true)?.DataContext == editing)
            return;

        _ = CommitRename(editing);
    }

    // Teardown-and-persist for an in-place sidebar rename. The rules for what
    // an empty name means, and what a commit writes, live in
    // SidebarRenameService; this is only the call into it.
    private Task CommitRename(SidebarItem item) =>
        _viewModel == null ? Task.CompletedTask : _viewModel.Rename.CommitAsync(item, _viewModel);

    // ── Drag-to-reorder (playlist view only) ────────────────────────────────────

    private async void OnRowReordered(object? sender, (TrackRowViewModel dragged, TrackRowViewModel? insertBefore) e)
    {
        if (_viewModel is not MainViewModel vm)
            return;
        if (vm.IsShowingQueue)
        {
            vm.Queue.Reorder(e.dragged, e.insertBefore);
            return;
        }
        if (vm.SelectedSidebarItem?.Playlist is not Playlist playlist)
            return;

        await vm.ReorderPlaylistTrack(playlist, e.dragged.Track, e.insertBefore?.Track);
    }

    // ── Device detail: the selected server's own settings ──────────────────────

    // Hosts (or clears) the settings panel for whichever server row is selected.
    // One panel per ViewModel, kept for as long as the ViewModel is - which is
    // now for the life of the session (see MainViewModel._serverSettings). A
    // panel takes its ViewModel in its constructor and subscribes to it there,
    // so rebuilding one over a ViewModel that already had one would leave the
    // old panel subscribed to it forever; keeping them paired is both the
    // cheaper and the correct thing, and it is what carries the pane's scroll
    // positions and the log document itself across a trip to another sidebar
    // row.
    private SettingsViewModel? _hostedServerSettings;
    private readonly Dictionary<SettingsViewModel, SettingsPanel> _serverSettingsPanels = new();

    private void UpdateServerSettingsPane()
    {
        var settings = _viewModel?.SelectedServerSettings;
        if (ReferenceEquals(settings, _hostedServerSettings))
        {
            UpdateServerSettingsMessage();
            return;
        }

        if (_hostedServerSettings != null)
            _hostedServerSettings.PropertyChanged -= OnServerSettingsPropertyChanged;
        _hostedServerSettings = settings;

        if (settings == null)
        {
            ServerSettingsHost.Content = null;
        }
        else
        {
            settings.PropertyChanged += OnServerSettingsPropertyChanged;
            if (!_serverSettingsPanels.TryGetValue(settings, out var panel))
            {
                // No MainViewModel: that argument is what adds this device's own
                // server picker to the Devices tab, and this panel is administering
                // the server, not this device (see SettingsPanel.RefreshDevicesTab).
                panel = new SettingsPanel(settings);
                // Inline, so no Cancel/OK pair and nothing to close - see
                // SettingsPanel.UseInlineChrome.
                panel.UseInlineChrome();
                _serverSettingsPanels[settings] = panel;
            }

            ServerSettingsHost.Content = panel;
        }

        UpdateServerSettingsMessage();
    }

    // The browser's Server Settings page, built the first time its sidebar row
    // is chosen and kept afterwards.
    //
    // Kept rather than rebuilt so moving to Songs and back does not throw away
    // the tab the reader was on or a half-typed edit, and because
    // SettingsPanel stops its own log poll when it leaves the visual tree (see
    // SetLogTabActive) - so an instance nobody is looking at costs nothing.
    //
    // Built lazily for a reason that is not performance: constructing it is
    // what starts the admin requests, and a tab that never opens this page
    // should never make one - an ordinary listener's tab is not an
    // administrator's and would only collect refusals.
    private SettingsPanel? _serverSettingsPage;

    private void UpdateServerSettingsPage()
    {
        if (_viewModel is not { IsShowingServerSettings: true } || _serverSettingsPage != null)
            return;

        try
        {
            // No MainViewModel, and inline chrome, for the same reasons the
            // device-detail pane's panel has neither: this administers the
            // server rather than this device, so the Devices tab's own server
            // picker does not belong in it, and a page has nothing to cancel
            // back to - only a Save. See UpdateServerSettingsPane.
            _serverSettingsPage = new SettingsPanel(App.CreateOriginServerSettings());
            _serverSettingsPage.UseInlineChrome();
            ServerSettingsPageHost.Content = _serverSettingsPage;
        }
        catch (Exception ex)
        {
            // Selecting a sidebar row must not take the tab down with it. The
            // page is then empty, which is worse than a message and better than
            // a crash; the same failure at startup is only logged too (see
            // App.OpenServerSettingsFromUrl).
            Logger.LogWarning(ex, "Could not build the server settings page");
        }
    }

    private void OnServerSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.IsLoaded) or nameof(SettingsViewModel.ErrorMessage))
            UpdateServerSettingsMessage();
    }

    // The panel only earns its place once the server has actually answered with
    // its settings. Until then this says why it has not: not paired, not
    // reachable, not yet approved, or - the common one - paired as an ordinary
    // listener rather than an administrator, which is the server's own refusal
    // message.
    //
    // "Not yet approved" is asked rather than assumed. It used to be the
    // fallback for everything else, so a server that had approved this device
    // long ago and then dropped off the network was reported as waiting to
    // approve it.
    // Written for a listener rather than the owner, since the owner is the one
    // person who never needs telling: what pairing gets them, and where the
    // code comes from - which is somebody else, so the message says who to ask.
    // The owner's own route is the code the server prints to its log while
    // nothing can administer it (see docs/SELF-HOSTING.md). Single-use and ten minutes is the server's rule,
    // see docs/SELF-HOSTING.md.
    private const string NotPairedMessage =
        "Pair with this server to stream and download its music on this device.\n\n"
        + "To pair, you need a pairing code or invite link from whoever runs this server. "
        + "If it's your own server and nothing is paired with it yet, it prints a pairing code in its log when it starts.\n\n"
        + "A code works once and expires after ten minutes, so use it soon after you get it.";

    private void UpdateServerSettingsMessage()
    {
        var vm = _viewModel;
        var settings = _hostedServerSettings;
        var loaded = settings is { IsLoaded: true };

        ServerSettingsHost.IsVisible = loaded;
        ServerSettingsMessage.IsVisible = !loaded;
        ServerSettingsMessage.Text =
            settings?.ErrorMessage is { Length: > 0 } error ? error :
            settings != null ? "Loading this server's settings…" :
            vm is null || !vm.IsSelectedDevicePaired ? NotPairedMessage :
            !vm.IsSelectedServerReachable ? "This server can't be reached right now. Its settings will appear here once it's back on the network." :
            vm.IsPairAwaitingApproval ? "Waiting for this server to approve this device." :
            "This server's settings can't be managed from this device.";
    }

    // Device-detail header's Pair/Unpair button - mirrors ServerPickerView's
    // ActionButton_Click (Settings' Devices tab), same confirmation copy,
    // just acting on SelectedDevice instead of a ServerRow.
    // Opening the selected server's browser settings page. All the work is in the
    // ViewModel (it has to sign a request against that server); this only exists
    // because a Button's Click is not a command binding.
    private void ServerSettingsButton_Click(object? sender, RoutedEventArgs e) =>
        _ = _viewModel?.OpenSelectedServerSettingsAsync();

    private async void PairActionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } vm)
            return;
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        // No SelectedDevice is an ordinary state for the paired server's row:
        // it stays pinned in the sidebar after the server drops off the
        // network, with no live endpoint behind it (see
        // DeviceSidebarSection.RemoveItem). Unpairing needs none - it only
        // forgets this side of the pairing - so only the pair path below asks
        // for one. Requiring it up front is what once left Unpair doing
        // nothing on exactly the row where it is most wanted.
        var device = vm.SelectedDevice;
        var alias = vm.SelectedSidebarItem?.Name ?? device?.Alias ?? "this server";

        if (vm.IsSelectedDevicePaired)
        {
            var confirmedUnpair = await ConfirmDialogWindow.ShowAsync(
                owner,
                $"Unpair From \"{alias}\"?",
                MainViewModel.UnpairConsequences(alias),
                "Unpair");
            if (confirmedUnpair)
                vm.UnpairServer();
            return;
        }

        // Pairing switches this device's own Songs/Albums view over to what
        // it syncs in from the server (see SettingsWindow's
        // CanManageLocalLibrary) - worth a clear warning before it happens,
        // same copy as ServerPickerView's own Pair confirmation.
        //
        // Nothing is being asked: a server is headless, so the admin-issued
        // code the user already holds is the whole of the approval, and the
        // copy says so rather than promising a prompt nobody will ever see.
        if (device == null)
            return;

        if (string.IsNullOrWhiteSpace(vm.PairingCode))
        {
            vm.PairingCodeError = "Enter the pairing code first.";
            return;
        }

        var confirmedPair = await ConfirmDialogWindow.ShowAsync(
            owner,
            $"Pair With \"{alias}\"?",
            $"This device's library view will be replaced by \"{alias}\"'s - your Songs/Albums list will show its library instead of managing its own. Your existing music files on this device will not be deleted. "
            + $"The pairing code you entered authorizes this device on \"{alias}\" immediately.",
            "Pair");
        if (confirmedPair)
            vm.PairWithServer(device, vm.PairingCode.Trim());
    }

    // Enter in the pairing-code box runs the same flow as the button - typing
    // a code and pressing Return is what a user does without thinking, and
    // having it do nothing reads as the box being broken.
    private void PairingCodeBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        PairActionButton_Click(sender, new RoutedEventArgs());
    }
}
