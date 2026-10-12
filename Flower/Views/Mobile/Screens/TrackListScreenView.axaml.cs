using System;
using System.Collections.Generic;
using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

using Flower.Services;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile.Screens;

public partial class TrackListScreenView : UserControl, ITrackRowHost
{
    // The rows/header this instance actually renders - deliberately NOT a
    // direct binding to Main.Rows/AlbumDetailRows/CurrentAlbumHeader on the
    // shared MobileMainViewModel (see the XAML's own comment). While this is
    // the CURRENT screen (ObserveLive), these track the live VM exactly as
    // the old direct bindings did. Once demoted to "one back" by
    // ScreenStackPanel (Freeze), they stop tracking anything and hold
    // whatever MobileNavigationFrame.FrozenRows/FrozenHeader captured at the
    // moment this screen was left - the mechanism that lets a kept-alive
    // instance sit there unchanged, ready to be revealed by a swipe, rather
    // than following whatever the live VM now shows (which, since Main.Rows
    // is a single shared collection, could by then be a completely different
    // album/playlist's tracks - see MobileNavigationFrame's own doc comment).
    public static readonly StyledProperty<IReadOnlyList<TrackRowViewModel>> DisplayRowsProperty =
        AvaloniaProperty.Register<TrackListScreenView, IReadOnlyList<TrackRowViewModel>>(
            nameof(DisplayRows), defaultValue: Array.Empty<TrackRowViewModel>());

    public static readonly StyledProperty<AlbumTileViewModel?> DisplayHeaderProperty =
        AvaloniaProperty.Register<TrackListScreenView, AlbumTileViewModel?>(nameof(DisplayHeader));

    // The header the virtualized list shows above its first row - a
    // playlist's, and nothing at all for the flat Songs list, which is the
    // whole library rather than one thing with a name and a cover. Separate
    // from DisplayHeader because the two go to different places: DisplayHeader
    // is the album screen's own, in its own scroller, and this one is handed
    // to the ListBox's template (ScreenScroll.Header).
    public static readonly StyledProperty<AlbumTileViewModel?> ListHeaderProperty =
        AvaloniaProperty.Register<TrackListScreenView, AlbumTileViewModel?>(nameof(ListHeader));

    public AlbumTileViewModel? ListHeader
    {
        get => GetValue(ListHeaderProperty);
        private set => SetValue(ListHeaderProperty, value);
    }

    public static readonly StyledProperty<bool> IsAlbumModeProperty =
        AvaloniaProperty.Register<TrackListScreenView, bool>(nameof(IsAlbumMode));

    // Same reasoning as IsAlbumMode/DisplayRows above, for TrackRowTemplate's
    // own drag-handle visibility (see that file) - it used to read
    // IsShowingPlaylistTracks straight off the shared DataContext, which is
    // exactly as wrong for a kept-alive one-back/one-forward instance as
    // reading IsShowingAlbumTrackList straight off it was for the row art:
    // briefly showing/hiding based on whatever screen is CURRENTLY live,
    // until the promotion/freeze catches up and it flips back - confirmed on
    // a real device as album art flashing in on every row of a revealed
    // album screen mid swipe-forward, then disappearing once the transition
    // committed and ObserveLive resynced it against the (by-then-correct)
    // live VM.
    public static readonly StyledProperty<bool> IsPlaylistModeProperty =
        AvaloniaProperty.Register<TrackListScreenView, bool>(nameof(IsPlaylistMode));

    // Wide enough to put an album's art beside its track list instead of
    // stacked above it - a phone in landscape, or a tablet either way up. 600
    // clears the widest phone in portrait (~430) with room to spare and sits
    // under the narrowest phone in landscape (~667), so the split is exactly
    // "turned sideways or bigger". Driven off this control's own measured
    // width below rather than a screen/orientation API: the layout only cares
    // how much room it actually got, which is also what makes it testable and
    // what makes a resized desktop window behave sensibly for free.
    private const double WideAlbumLayoutMinWidth = 600;

    public static readonly StyledProperty<bool> IsWideAlbumLayoutProperty =
        AvaloniaProperty.Register<TrackListScreenView, bool>(nameof(IsWideAlbumLayout));

    public bool IsWideAlbumLayout
    {
        get => GetValue(IsWideAlbumLayoutProperty);
        private set => SetValue(IsWideAlbumLayoutProperty, value);
    }

    // Must match the vertical Margin on the pinned art in the XAML - the art
    // is square and sized off the height left over once its own margin is
    // taken out, so the two numbers have to agree or it overflows the screen
    // by exactly the difference. 16 below, and 16 below the gap the header
    // band leaves above, since the screen runs up under the see-through header
    // (ScreenSlot.HeaderHeight/ContentGap).
    private const double PinnedArtInset =
        16 + Flower.Controls.ScreenSlot.HeaderHeight + Flower.Controls.ScreenSlot.ContentGap + 16;

    // ...and how much of the width the art is allowed to take. A landscape
    // phone is height-bound, so the cap never binds there and the art simply
    // fills the screen top to bottom, which is the point. It exists for the
    // shape where height is the *larger* dimension - a tablet held upright,
    // or a tall desktop window - where filling the height would leave the
    // songs a column an inch wide.
    private const double PinnedArtMaxWidthFraction = 0.4;

    public static readonly StyledProperty<double> PinnedArtSizeProperty =
        AvaloniaProperty.Register<TrackListScreenView, double>(nameof(PinnedArtSize));

    /// <summary>
    /// The side of the album art pinned beside the track list in the wide
    /// layout. Computed here rather than left to the layout system because
    /// nothing in a Grid will size a square off the height it was given: the
    /// art sits in an Auto column, which measures with unconstrained width.
    /// </summary>
    public double PinnedArtSize
    {
        get => GetValue(PinnedArtSizeProperty);
        private set => SetValue(PinnedArtSizeProperty, value);
    }

    public IReadOnlyList<TrackRowViewModel> DisplayRows
    {
        get => GetValue(DisplayRowsProperty);
        private set => SetValue(DisplayRowsProperty, value);
    }

    public AlbumTileViewModel? DisplayHeader
    {
        get => GetValue(DisplayHeaderProperty);
        private set => SetValue(DisplayHeaderProperty, value);
    }

    public bool IsAlbumMode
    {
        get => GetValue(IsAlbumModeProperty);
        private set => SetValue(IsAlbumModeProperty, value);
    }

    public bool IsPlaylistMode
    {
        get => GetValue(IsPlaylistModeProperty);
        private set => SetValue(IsPlaylistModeProperty, value);
    }

    public static readonly StyledProperty<bool> IsSongsModeProperty =
        AvaloniaProperty.Register<TrackListScreenView, bool>(nameof(IsSongsMode));

    /// <summary>
    /// Whether this is the flat Songs list - the one shape of this screen in
    /// title order, and so the one with an A-Z bar. From the frame when
    /// frozen, like IsAlbumMode, for the same reason.
    /// </summary>
    public bool IsSongsMode
    {
        get => GetValue(IsSongsModeProperty);
        private set => SetValue(IsSongsModeProperty, value);
    }

    public static readonly StyledProperty<bool> ShowsRowArtistProperty =
        AvaloniaProperty.Register<TrackListScreenView, bool>(nameof(ShowsRowArtist), defaultValue: true);

    /// <summary>
    /// Whether a track row should print its artist under the title - false
    /// when the whole list shares one artist (see ITrackRowHost). Derived
    /// from DisplayRows below rather than from the live ViewModel, so a
    /// frozen one-back screen answers for the rows it is actually showing.
    /// </summary>
    public bool ShowsRowArtist
    {
        get => GetValue(ShowsRowArtistProperty);
        private set => SetValue(ShowsRowArtistProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty)
        {
            IsWideAlbumLayout = Bounds.Width >= WideAlbumLayoutMinWidth;
            PinnedArtSize = Math.Max(0, Math.Min(
                Bounds.Height - PinnedArtInset,
                Bounds.Width * PinnedArtMaxWidthFraction));
        }
        else if (change.Property == DisplayRowsProperty)
        {
            ShowsRowArtist = TrackRowViewModel.ArtistIsWorthShowing(DisplayRows);
        }
        else if (change.Property == DisplayHeaderProperty)
        {
            WatchCover(DisplayHeader);
        }
    }

    // An album's page is washed in its cover's colour by the same rule as Now
    // Playing's sheet (ArtBackdrop.SheetBrush), so the two read as one thing:
    // with no cover, the theme's pressed accent put through the same
    // darkening. Follows this screen's own header rather than the live one,
    // like everything else here, and the header's art, which loads after the
    // header itself does. Again when the theme changes, the fallback being the
    // theme's.
    private AlbumTileViewModel? _cover;

    private void WatchCover(AlbumTileViewModel? header)
    {
        if (_cover != null)
            _cover.PropertyChanged -= Cover_PropertyChanged;
        _cover = header;
        if (_cover != null)
            _cover.PropertyChanged += Cover_PropertyChanged;
        ApplyCover();
    }

    private void Cover_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlbumTileViewModel.AlbumArt))
            ApplyCover();
    }

    private void ApplyCover()
    {
        AlbumPage.Background = ArtBackdrop.SheetBrush(ArtBackdrop.DominantColor(_cover?.AlbumArt));
        BackdropImage.Source = ArtBackdrop.Soft(_cover?.AlbumArt);
        PublishPage();
    }

    private MobileMainViewModel? _observedVm;

    public TrackListScreenView()
    {
        InitializeComponent();

        LayoutUpdated += (_, _) =>
        {
            SizeBackdrop();
            PublishPage();
        };
        ActualThemeVariantChanged += (_, _) => ApplyCover();

        // Drag-to-reorder, for a playlist's tracks - the handle it starts
        // from is only on screen in IsPlaylistMode.
        _ = new TrackRowDragReorder(TrackListBox, this, DropIndicator, (dragged, insertBefore) =>
        {
            if (DataContext is MobileMainViewModel vm)
                vm.ReorderCurrentPlaylistTrack(dragged.Track, insertBefore?.Track);
        });

        // Songs are sorted on the sort-as value of their title, artist or album
        // with punctuation stripped (TrackListBuilder), so the letter is worked
        // out the same way. Only ever asked while live: a frozen screen is the
        // one behind, and cannot be touched.
        IndexBar.IndexOfLetter = letter => _observedVm is { } vm
            ? AlphabetIndex.FirstIndexFor(DisplayRows,
                r => AlphabetIndex.LetterOf(vm.SongLetterText(r.Track), skipPunctuation: true), letter,
                descending: vm.SongsRunZToA)
            : -1;
    }

    // Called by ScreenStackPanel whenever this instance is the CURRENT
    // screen - subscribes to the shared VM so DisplayRows/DisplayHeader/
    // IsAlbumMode keep following it live, exactly like the old direct
    // bindings did (a rescan/download completing while sitting on this
    // screen still needs to show up immediately, not just at the next
    // navigation change).
    public void ObserveLive(MobileMainViewModel vm)
    {
        if (ReferenceEquals(_observedVm, vm))
        {
            RefreshFromLive();
            return;
        }

        Detach();
        _observedVm = vm;
        vm.PropertyChanged += OnObservedVmChanged;
        vm.Main.PropertyChanged += OnObservedVmChanged;
        RefreshFromLive();
    }

    // Called by ScreenStackPanel when this instance is demoted to "one
    // back" - stops following the live VM and freezes at exactly what the
    // frame captured on the way out. See this class's own DisplayRows doc
    // comment for why.
    public void Freeze(MobileNavigationFrame frame)
    {
        Detach();
        DisplayRows = frame.FrozenRows ?? Array.Empty<TrackRowViewModel>();
        DisplayHeader = frame.FrozenHeader;
        IsAlbumMode = frame.IsAlbumTrackList;
        IsPlaylistMode = frame.IsPlaylistTrackList;
        IsSongsMode = frame.Tab == MobileTab.Songs && !IsAlbumMode;
        ListHeader = IsPlaylistMode ? frame.FrozenHeader : null;
    }

    // ScreenControlFactory calls this when evicting a cached instance from
    // its LRU, so a control nobody references anymore doesn't leak a live
    // subscription to the VM.
    public void Detach()
    {
        if (_observedVm == null)
            return;
        _observedVm.PropertyChanged -= OnObservedVmChanged;
        _observedVm.Main.PropertyChanged -= OnObservedVmChanged;
        _observedVm = null;
    }

    private void OnObservedVmChanged(object? sender, PropertyChangedEventArgs e) => RefreshFromLive();

    private void RefreshFromLive()
    {
        if (_observedVm == null)
            return;
        IsAlbumMode = _observedVm.IsShowingAlbumTrackList;
        IsPlaylistMode = _observedVm.IsShowingPlaylistTracks;
        IsSongsMode = _observedVm.SelectedTab == MobileTab.Songs && !IsAlbumMode;
        // Letters while sorted on a name, dots on Year or Date Added. Left as
        // they were on Freeze: the sort cannot change behind a pushed screen.
        IndexBar.ShowsLetters = _observedVm.SongsAreAlphabetical;
        IndexBar.LettersDescending = _observedVm.SongsRunZToA;
        DisplayRows = IsAlbumMode ? _observedVm.AlbumDetailRows : _observedVm.Main.Rows;
        DisplayHeader = _observedVm.CurrentAlbumHeader;
        ListHeader = _observedVm.CurrentPlaylistHeader;
    }

    // The blurred cover (Backdrop in the XAML) is the page's and stays put,
    // but fades out under the header's words, wherever they wrap to - so it
    // is as tall as the stacked header stands with nothing scrolled: the
    // scroller's inset above it, then the header itself. From layout bounds,
    // which a scroll or a pull does not move.
    private void SizeBackdrop()
    {
        var height = AlbumContent.Margin.Top + StackedHeader.Bounds.Bottom;
        if (Backdrop.Height != height)
            Backdrop.Height = height;
    }

    // What the page draws, handed to the slot so the buttons over it can be
    // filled with it (ScreenSlot.PageDrawing): the cover's colour across the
    // whole of it, and the blurred cover on top as the Backdrop element draws
    // it - the same picture, placed, faded and dimmed the same way, so the
    // two cannot drift apart. Rebuilt only when one of the things it is made
    // of changes, since this runs after every layout pass.
    private (bool Album, Size Page, Rect Backdrop, IBrush? Sheet, IImage? Cover)? _published;

    private void PublishPage()
    {
        var backdrop = Backdrop.IsVisible ? Backdrop.Bounds : default;
        var made = (IsAlbumMode, AlbumPage.Bounds.Size, backdrop, AlbumPage.Background, BackdropImage.Source);
        if (_published == made)
            return;
        _published = made;
        Flower.Controls.ScreenSlot.SetPageDrawing(this, IsAlbumMode ? PageDrawing(backdrop) : null);
    }

    private Drawing? PageDrawing(Rect backdrop)
    {
        if (AlbumPage.Background is not { } sheet)
            return null;

        var page = new DrawingGroup();
        page.Children.Add(new GeometryDrawing
        {
            Geometry = new RectangleGeometry(new Rect(AlbumPage.Bounds.Size)),
            Brush = sheet,
        });

        if (BackdropImage.Source is IImageBrushSource cover && backdrop is { Width: > 0, Height: > 0 })
        {
            // The dimming on the brush rather than the group: a DrawingGroup
            // with both an Opacity and an OpacityMask applies the opacity
            // twice, which drew the cover at a third of its strength.
            var blurred = new DrawingGroup
            {
                OpacityMask = FadeMaskOver(backdrop),
                ClipGeometry = new RectangleGeometry(backdrop),
            };
            blurred.Children.Add(new GeometryDrawing
            {
                Geometry = new RectangleGeometry(backdrop),
                Brush = new ImageBrush(cover) { Stretch = Stretch.UniformToFill, Opacity = BackdropImage.Opacity },
            });
            page.Children.Add(blurred);
        }

        return page;
    }

    // The Backdrop's fade (AppBackdropFadeMask, top to bottom of whatever it
    // masks), pinned to where the Backdrop is on the page: inside a drawing,
    // a relative brush would answer to the drawing's bounds instead.
    private IBrush? FadeMaskOver(Rect backdrop)
    {
        if (!this.TryFindResource("AppBackdropFadeMask", ActualThemeVariant, out var found)
            || found is not LinearGradientBrush fade)
            return null;

        var pinned = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(backdrop.X, backdrop.Top, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(backdrop.X, backdrop.Bottom, RelativeUnit.Absolute),
        };
        foreach (var stop in fade.GradientStops)
            pinned.GradientStops.Add(new GradientStop(stop.Color, stop.Offset));
        return pinned;
    }
}
