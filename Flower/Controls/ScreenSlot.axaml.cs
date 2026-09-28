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
        AddHandler(RubberBandScroll.PulledDownEvent, Screen_PulledDown);
        AddHandler(RubberBandScroll.PullingDownEvent, Screen_PullingDown);
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
        if (sender is not Button { DataContext: string query } || DataContext is not MobileMainViewModel vm)
            return;
        vm.ApplySearchSuggestionCommand.Execute(query);
        TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
    }

    // ── The screen menu ───────────────────────────────────────────────────

    // Rebuilt on every opening, for the screen as it is right then: which
    // entries a screen has, which sort is ticked and whether there is
    // anything left to download all change underneath it between openings.
    private void ScreenMenu_Opening(object? sender, EventArgs e)
    {
        ScreenMenuItems.Children.Clear();
        if (DataContext is not MobileMainViewModel vm || Frame is not { } frame)
            return;

        var first = true;
        foreach (var section in vm.BuildScreenMenu(frame))
        {
            if (!first)
            {
                ScreenMenuItems.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 4),
                    [!Border.BackgroundProperty] = new DynamicResourceExtension("AppPrimaryBorderBrush"),
                });
            }
            first = false;

            if (section.Header is { } header)
            {
                ScreenMenuItems.Children.Add(new TextBlock
                {
                    Text = header,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Opacity = 0.5,
                    Margin = new Thickness(16, 8, 16, 2),
                });
            }

            foreach (var entry in section.Entries)
                ScreenMenuItems.Children.Add(MenuRow(entry));
        }
    }

    // Opening and closing, the card grows out of the button's corner and
    // folds back into it - from there because that is where the menu comes
    // from. Quick: it is in the way of the tap that follows.
    // Built by XAML, so there is no constructor to inject one through.
    private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(ScreenSlot).FullName!);

    private static readonly TimeSpan MenuOpenDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan MenuCloseDuration = TimeSpan.FromMilliseconds(120);
    private const double MenuFoldedScale = 0.6;

    // Set while the closing animation runs, and while the Hide that follows
    // it goes through - so that Hide is let past, and a second close asked
    // for mid-animation (a tap on the backdrop, then another) starts nothing.
    private bool _isMenuClosing;

    private FlyoutPresenter? MenuCard => ScreenMenuItems.FindAncestorOfType<FlyoutPresenter>();

    private void ScreenMenu_Opened(object? sender, EventArgs e)
    {
        if (MenuCard is not { } card)
            return;
        var scale = new ScaleTransform(MenuFoldedScale, MenuFoldedScale);
        card.RenderTransformOrigin = new RelativePoint(1, 0, RelativeUnit.Relative);
        card.RenderTransform = scale;
        // Folded before the first frame is drawn, not on the animation's first
        // tick, which can land after it - a menu that flashed open at full
        // size and then shrank to grow again.
        card.Opacity = 0;
        MenuAnimation(MenuOpenDuration, new CubicEaseOut(), from: (MenuFoldedScale, 0), to: (1, 1))
            .RunAsync(card).Forget(Logger, "Screen menu opening");
    }

    private async void ScreenMenu_Closing(object? sender, CancelEventArgs e)
    {
        if (_isMenuClosing)
            return;
        if (MenuCard is not { } card)
            return;

        e.Cancel = true;
        _isMenuClosing = true;
        try
        {
            await MenuAnimation(MenuCloseDuration, new CubicEaseIn(), from: (1, 1), to: (MenuFoldedScale, 0))
                .RunAsync(card);
            MenuButton.Flyout?.Hide();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Screen menu closing animation failed");
            MenuButton.Flyout?.Hide();
        }
        finally
        {
            _isMenuClosing = false;
        }
    }

    private static Animation MenuAnimation(TimeSpan duration, Easing easing,
        (double Scale, double Opacity) from, (double Scale, double Opacity) to) => new()
    {
        Duration = duration,
        Easing = easing,
        FillMode = FillMode.Forward,
        Children =
        {
            new KeyFrame
            {
                Cue = new Cue(0),
                Setters =
                {
                    new Setter(ScaleTransform.ScaleXProperty, from.Scale),
                    new Setter(ScaleTransform.ScaleYProperty, from.Scale),
                    new Setter(OpacityProperty, from.Opacity),
                },
            },
            new KeyFrame
            {
                Cue = new Cue(1),
                Setters =
                {
                    new Setter(ScaleTransform.ScaleXProperty, to.Scale),
                    new Setter(ScaleTransform.ScaleYProperty, to.Scale),
                    new Setter(OpacityProperty, to.Opacity),
                },
            },
        },
    };

    private Button MenuRow(ScreenMenuEntry entry)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 14 };
        var icon = new MaterialIcon { Kind = entry.Icon, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        var label = new TextBlock { Text = entry.Label, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        // Red, like the same entries on the song and album menus.
        if (entry.IsDestructive)
        {
            icon[!MaterialIcon.ForegroundProperty] = new DynamicResourceExtension("AppErrorBrush");
            label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("AppErrorBrush");
        }
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        // The sort in use says which way it runs; picking it again turns it
        // round (MobileMainViewModel.ChooseSort).
        if (entry.Direction is { } direction)
        {
            var arrow = new MaterialIcon
            {
                Kind = direction == ListSortDirection.Ascending ? MaterialIconKind.ArrowUp : MaterialIconKind.ArrowDown,
                Width = 20,
                Height = 20,
                VerticalAlignment = VerticalAlignment.Center,
                [!MaterialIcon.ForegroundProperty] = new DynamicResourceExtension("AppAccentBrush"),
            };
            Grid.SetColumn(arrow, 2);
            row.Children.Add(arrow);
        }

        var button = new Button { Content = row, Classes = { "screenMenuRow" } };
        // Closed first, so a sheet the entry raises (Settings, Add to
        // Playlist) comes up over the screen rather than under the menu.
        button.Click += (_, _) =>
        {
            MenuButton.Flyout?.Hide();
            entry.Invoke();
        };
        return button;
    }
}
