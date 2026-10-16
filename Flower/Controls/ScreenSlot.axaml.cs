using System;
using System.ComponentModel;

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;

using Material.Icons;
using Material.Icons.Avalonia;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Services;
using Flower.ViewModels.Mobile;

namespace Flower.Controls;

// One screen's own materialized content plus its sliding header - see the
// XAML's own doc comment for why this exists. Frame carries the per-screen
// header state, which is now down to one question (is this the Search tab, and
// so is the box showing); genuinely live/global state - the query itself -
// binds straight through to DataContext instead, which ScreenStackPanel sets
// to the shared VM on every slot it builds.
public partial class ScreenSlot : UserControl
{
    // The header band's height - the XAML's first row. The screen runs up
    // under it, so what scrolls starts this far down (MobileMainView's
    // ScreenScrollInset) and what does not keeps clear of it on its own.
    public const double HeaderHeight = 52;

    // ...plus this much before anything of the screen's own begins. The band
    // is see-through and the back button sits in it, so a title or a first row
    // that started flush against the band's edge started right under that
    // button. Every screen takes both together (MobileMainView's
    // ScreenScrollInset, and the pinned album art in TrackListScreenView,
    // which is the one piece of screen content outside a scroller).
    public const double ContentGap = 12;

    public static readonly StyledProperty<MobileNavigationFrame?> FrameProperty =
        AvaloniaProperty.Register<ScreenSlot, MobileNavigationFrame?>(nameof(Frame));

    public MobileNavigationFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public ScreenSlot()
    {
        InitializeComponent();
        // The menu holds this screen's entries, as the screen is right then.
        ScreenMenu.Sections = () => DataContext is MobileMainViewModel vm && Frame is { } frame
            ? vm.BuildScreenMenu(frame)
            : [];
        AddHandler(RubberBandScroll.PulledDownEvent, Screen_PulledDown);
        AddHandler(RubberBandScroll.PullingDownEvent, Screen_PullingDown);
        HeaderBand.LayoutUpdated += (_, _) => FillPills();
    }

    // Inserts the wrapped screen control into this slot's content area.
    // Defensive detach first - ScreenControlFactory's LRU cache can hand the
    // same raw Control back out through a brand-new ScreenSlot wrapper later
    // (e.g. revisiting the same album), and Avalonia throws if a Control
    // still has a prior visual parent.
    public void SetContent(Control content)
    {
        if (content.Parent is ContentControl oldHost && !ReferenceEquals(oldHost, ContentHost))
            oldHost.Content = null;
        ContentHost.Content = content;
        FillPills();
    }

    // ── The page under the band ───────────────────────────────────────────

    /// <summary>
    /// What a screen paints behind everything else, in its own coordinates,
    /// for a screen whose page is more than the plain background - an album's,
    /// washed in its cover's colour with the cover blurred at the top
    /// (TrackListScreenView). Null for the plain page.
    /// </summary>
    public static readonly AttachedProperty<Drawing?> PageDrawingProperty =
        AvaloniaProperty.RegisterAttached<ScreenSlot, Control, Drawing?>("PageDrawing");

    public static Drawing? GetPageDrawing(Control screen) => screen.GetValue(PageDrawingProperty);

    public static void SetPageDrawing(Control screen, Drawing? value) => screen.SetValue(PageDrawingProperty, value);

    static ScreenSlot()
    {
        PageDrawingProperty.Changed.AddClassHandler<Control>((screen, _) =>
            screen.FindAncestorOfType<ScreenSlot>()?.FillPills());
    }

    /// <summary>The screen this slot shows - the one PageDrawing is read off.</summary>
    public Control? Screen => ContentHost.Content as Control;

    // Over a painted page the band's circles and oval are filled with it:
    // each shows exactly the patch of page it covers, so it reads as
    // see-through while the songs scrolling up under it stay hidden. Again
    // after every layout, since the oval comes and goes and the band moves
    // with the safe area.
    private void FillPills()
    {
        var screen = Screen;
        var page = screen is null ? null : GetPageDrawing(screen);
        foreach (var pill in new[] { BackPill, ScreenMenu.Pill, FilterOval })
        {
            var at = page is null || screen is null || !pill.IsVisible ? null : pill.TranslatePoint(default, screen);
            FillWithPage(pill, page, at is { } point ? new Rect(point, pill.Bounds.Size) : null);
        }
    }

    /// <summary>
    /// Fills a control floating over a painted page with the patch of that
    /// page under it (<paramref name="patch"/>, in the page's coordinates),
    /// or gives it back its styled fill when there is no page or no patch.
    /// A brush over the page's own drawing, cut to the control's place,
    /// rather than a copy of the page's controls inside each one - the page
    /// is still, so where the control sits is all it takes. Used for the
    /// band's buttons here and for the mini player and tab oval over the
    /// bottom of the screens (MobileMainView).
    /// </summary>
    public static void FillWithPage(Border pill, Drawing? page, Rect? patch)
    {
        if (page is null || patch is not { } source)
        {
            if (pill.IsSet(Border.BackgroundProperty))
                pill.ClearValue(Border.BackgroundProperty);
            return;
        }

        if (pill.Background is DrawingBrush current && ReferenceEquals(current.Drawing, page) && current.SourceRect.Rect == source)
            return;
        pill.Background = new DrawingBrush(page)
        {
            SourceRect = new RelativeRect(source, RelativeUnit.Absolute),
            Stretch = Stretch.Fill,
        };
    }

    // ── The filter oval ───────────────────────────────────────────────────

    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<ScreenSlot, bool>(nameof(IsLive));

    /// <summary>
    /// Whether this slot is the screen being used, and so shows the filter as
    /// it is now (MobileMainViewModel.ScreenFilter) and takes what is typed.
    /// Every other slot - the one kept alive behind it for a swipe back, and
    /// the one being left while the next screen gets ready and slides over it
    /// - shows the filter its Frame was left with, unchanging.
    /// </summary>
    public bool IsLive
    {
        get => GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    public static readonly StyledProperty<bool> ShowsBackButtonProperty =
        AvaloniaProperty.Register<ScreenSlot, bool>(nameof(ShowsBackButton));

    /// <summary>
    /// Whether this screen has one behind it to go back to - set by
    /// ScreenStackPanel for each slot, since the screen kept alive behind the
    /// current one for a swipe has its own answer.
    /// </summary>
    public bool ShowsBackButton
    {
        get => GetValue(ShowsBackButtonProperty);
        set => SetValue(ShowsBackButtonProperty, value);
    }

    // Plays the same slide-off a swipe back does rather than cutting straight
    // to the screen underneath - see ScreenStackPanel.AnimateGoBack.
    private void BackButton_Click(object? sender, RoutedEventArgs e) =>
        this.FindAncestorOfType<ScreenStackPanel>()?.AnimateGoBack();

    public static readonly StyledProperty<bool> IsFilterShownProperty =
        AvaloniaProperty.Register<ScreenSlot, bool>(nameof(IsFilterShown));

    public bool IsFilterShown
    {
        get => GetValue(IsFilterShownProperty);
        private set => SetValue(IsFilterShownProperty, value);
    }

    public static readonly StyledProperty<bool> IsOvalShownProperty =
        AvaloniaProperty.Register<ScreenSlot, bool>(nameof(IsOvalShown));

    /// <summary>
    /// Whether the oval in the header shows at all: with the filter in it
    /// while that is open or being pulled open, and always on Search, where it
    /// holds the search box.
    /// </summary>
    public bool IsOvalShown
    {
        get => GetValue(IsOvalShownProperty);
        private set => SetValue(IsOvalShownProperty, value);
    }

    // Set while the box's text is being written from the view model or the
    // frame, so that the write is not taken for typing and sent straight back.
    private bool _showingFilter;

    private MobileMainViewModel? _vm;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WatchViewModel(DataContext as MobileMainViewModel);
        ShowFilter();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        WatchViewModel(null);
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot != null)
            WatchViewModel(DataContext as MobileMainViewModel);
        ShowFilter();
    }

    private void WatchViewModel(MobileMainViewModel? vm)
    {
        if (_vm != null)
            _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = vm;
        if (_vm != null)
            _vm.PropertyChanged += Vm_PropertyChanged;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsLive && e.PropertyName == nameof(MobileMainViewModel.ScreenFilter))
            ShowFilter();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsLiveProperty || change.Property == FrameProperty)
            ShowFilter();
    }

    // Null is the oval closed; "" is open with nothing typed yet.
    private string? ShownFilter => IsLive ? _vm?.ScreenFilter : Frame?.ScreenFilter;

    // How far the pull under way has gone towards opening the filter, 0 to 1
    // (RubberBandScroll.PullingDownEvent). While the filter is closed, the
    // oval shows at that much opacity - the more the pull, the more it is
    // there - and takes no touches, being only a preview of what letting go
    // opens.
    private double _pullProgress;

    private void ShowFilter()
    {
        var filter = ShownFilter;
        var isSearch = Frame is { IsSearchScreen: true };
        var isOpen = filter != null && Frame is { CanFilter: true };
        var isPeeking = !isOpen && IsLive && Frame is { CanFilter: true } && _pullProgress > 0;
        IsFilterShown = isOpen || isPeeking;
        IsOvalShown = IsFilterShown || isSearch;
        FilterOval.IsVisible = IsOvalShown;
        FilterOval.Opacity = isPeeking ? _pullProgress : 1;
        FilterOval.IsHitTestVisible = !isPeeking;
        FilterBox.PlaceholderText = PlaceholderFor(Frame?.ScreenKind);

        if ((FilterBox.Text ?? "") == (filter ?? ""))
            return;
        _showingFilter = true;
        FilterBox.Text = filter;
        _showingFilter = false;
    }

    private static string PlaceholderFor(MobileScreenKind? kind) => kind switch
    {
        MobileScreenKind.ArtistPicker => "Filter artists",
        MobileScreenKind.PlaylistPicker => "Filter playlists",
        MobileScreenKind.TrackList => "Filter songs",
        _ => "Filter albums",
    };

    // Opened by pulling this screen down from its top, ready to type in:
    // posted, because the box cannot take focus until the layout pass that
    // shows it has run, and focus is what raises the keyboard. A pull with the
    // oval already open puts the keyboard back up for it.
    //
    // The oval sits in the header band, so opening it moves nothing below.
    private void Screen_PulledDown(object? sender, RoutedEventArgs e)
    {
        if (!IsLive || _vm == null || !_vm.OpenScreenFilter())
            return;
        Dispatcher.UIThread.Post(() =>
        {
            FilterBox.Focus();
            FilterBox.CaretIndex = FilterBox.Text?.Length ?? 0;
        });
    }

    private void Screen_PullingDown(object? sender, PullingDownEventArgs e)
    {
        if (e.Progress == _pullProgress)
            return;
        _pullProgress = e.Progress;
        ShowFilter();
    }

    private void FilterBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_showingFilter || !IsLive || _vm == null || _vm.ScreenFilter == null)
            return;
        _vm.ScreenFilter = FilterBox.Text ?? "";
    }

    // Leaving the box with nothing typed in it closes the oval - see
    // MobileMainViewModel.CloseScreenFilterIfEmpty. Only the live screen's:
    // a kept-alive one shows the filter its frame was left with, and is not
    // this box's to close.
    private void FilterBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (IsLive)
            _vm?.CloseScreenFilterIfEmpty();
    }

    // Whether `visual` is part of this slot's filter oval - the box, its x.
    // A press anywhere else, while the filter is empty, closes it; see
    // MobileMainView.CloseEmptyFilterOnPressElsewhere.
    public bool IsInFilterOval(Visual? visual) =>
        visual != null && (ReferenceEquals(visual, FilterOval) || FilterOval.IsVisualAncestorOf(visual));

    // Return puts the keyboard away. The oval stays if something is typed,
    // and the list stays cut by it - only the x closes it then.
    private void FilterBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
    }

    public void FocusSearchBox() => Dispatcher.UIThread.Post(() => SearchTabBox.Focus());

    // Leaving the box with something in it is what makes it a search worth
    // remembering - a result tapped, the keyboard put away, the tab changed.
    private void SearchTabBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MobileMainViewModel vm)
            return;
        vm.IsSearchBoxFocused = false;
        vm.RememberSearch();
    }

    private void SearchTabBox_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is MobileMainViewModel vm)
            vm.IsSearchBoxFocused = true;
    }

    // The oval's x, on Search: empties the box, and leaves the keyboard as it
    // was - it takes no focus, so a box being typed in stays that way.
    private void ClearSearch_Click(object? sender, RoutedEventArgs e) => SearchTabBox.Text = "";

    // Return is the keyboard's own "that is what I am looking for": it puts the
    // keyboard away, and the results are already showing underneath.
    private void SearchTabBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
    }

    // The query is replaced before the box lets go of focus, so it is the
    // chosen search that is remembered on the way out rather than the few
    // letters typed to find it.
    private void Suggestion_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SearchSuggestion suggestion } || DataContext is not MobileMainViewModel vm)
            return;
        vm.ApplySearchSuggestionCommand.Execute(suggestion.Text);
        TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
    }
}
