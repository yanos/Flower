using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

using Flower.Controls;
using Flower.Services;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile;

public partial class MobileMainView : UserControl
{
    public MobileMainView()
    {
        InitializeComponent();
        ScreenStack.Settled += (_, _) =>
        {
            UpdateBackPill();
            EmptyStateHost.IsVisible = true;
        };
        ScreenStack.Moving += (_, _) => EmptyStateHost.IsVisible = false;

        MiniPlayerHost.RenderTransform = _miniPlayerTransform;
        NowPlayingSheet.PropertyChanged += (_, e) =>
        {
            if (e.Property == SlidingSheet.IsOpenProperty)
                TuckMiniPlayer(NowPlayingSheet.IsOpen);
        };
    }

    // ── The mini player under the tab oval ────────────────────────────────
    //
    // Now Playing keeps the tab oval over it but not the mini player, which is
    // only a smaller copy of the sheet. So the mini player slides down behind
    // the tab oval as the sheet opens and back up out of it as the sheet
    // closes, both alongside the sheet's own slide rather than after it: the
    // oval is drawn over the sheet, so there is nothing to wait for.
    //
    // It comes to rest centred on the tab oval, which is taller and the same
    // length, so it is wholly behind it. Faded out over the end of the way down
    // and in over the start of the way up, or its shadow would still ring the
    // tab oval while it sits there. A RenderTransform rather than a margin, so
    // BottomChrome keeps its height and ScreenScrollInset does not move under
    // the sheet.
    //
    // A manual AnimationClock tween, like SlidingSheet's, and for its reason:
    // an Avalonia Animation of a TranslateTransform throws on real iOS.
    private const double MiniPlayerTuckMs = 280;

    // The share of the slide the fade takes, at the end going down and at the
    // start coming up - by then or from then on, most of it is behind the oval.
    private const double MiniPlayerFadeShare = 0.35;

    private readonly TranslateTransform _miniPlayerTransform = new();

    // From where the mini player sits to centred on the tab oval. Both are
    // children of BottomChrome, so their bounds share one origin.
    private double MiniPlayerTuckDepth => TabBar.Bounds.Center.Y - MiniPlayerHost.Bounds.Center.Y;

    private IDisposable? _miniPlayerEasing;

    private void TuckMiniPlayer(bool tucked)
    {
        _miniPlayerEasing?.Dispose();
        MiniPlayerHost.IsHitTestVisible = !tucked;

        var from = _miniPlayerTransform.Y;

        _miniPlayerEasing = AnimationClock.Current.Subscribe(elapsed =>
        {
            // Read every frame rather than once: a mini player that has only
            // just appeared (a track started a moment ago) has not been laid
            // out yet when the sheet opens, and its bounds say it is nowhere.
            var depth = MiniPlayerTuckDepth;
            var to = tucked ? depth : 0;
            var t = Math.Min(1.0, elapsed.TotalMilliseconds / MiniPlayerTuckMs);
            var p = 1 - Math.Pow(1 - t, 3);
            _miniPlayerTransform.Y = from + (to - from) * p;

            // Opacity off how far down it is rather than off time, so a reversal
            // halfway through picks up the fade where the slide actually is.
            var down = depth > 0 ? _miniPlayerTransform.Y / depth : (tucked ? 1 : 0);
            MiniPlayerHost.Opacity = Math.Clamp((1 - down) / MiniPlayerFadeShare, 0, 1);

            if (t >= 1.0)
            {
                _miniPlayerEasing?.Dispose();
                _miniPlayerEasing = null;
            }
        });
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UpdateBackPill();
    }

    // Only once the screen has finished arriving: CanGoBack changes as a
    // navigation starts, so a binding to it would pop the button in over a
    // screen still sliding into place. See ScreenStackPanel.Settled.
    private void UpdateBackPill() =>
        BackPill.IsVisible = DataContext is MobileMainViewModel { CanGoBack: true };

    private const double TabBarInsetOverlap = 14;
    private const double TabBarMinimumBottomMargin = 12;

    // Past the top of the mini player, so a list's last row comes to rest a
    // little above it rather than touching it.
    private const double BottomChromeClearance = 8;

    // The screens' scrollers leave this much room past their last item (the
    // screenScroll style), which has to follow the floating stack's height as
    // the mini player appears and the safe area changes. The top is the
    // see-through header band plus the gap after it, neither of which moves.
    //
    // A mini player tucked under the tab oval follows it too, or one that
    // appeared while the sheet was up would be left where the stack used to be.
    private void BottomChrome_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        Resources["ScreenScrollInset"] = new Thickness(0, ScreenSlot.HeaderHeight + ScreenSlot.ContentGap, 0, e.NewSize.Height + BottomChromeClearance);
        if (NowPlayingSheet.IsOpen && _miniPlayerEasing == null)
            _miniPlayerTransform.Y = MiniPlayerTuckDepth;
    }

    // The Now Playing sheet runs under the tab oval, so its content stops this
    // far above it. More than a list's last row gets: a list scrolls on under
    // the oval anyway, where the volume slider is the end of the sheet and
    // wants room to read as the end rather than as crowded against the tabs.
    private const double NowPlayingTabBarClearance = 28;

    private void TabBar_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateNowPlayingInset();

    private void UpdateNowPlayingInset() =>
        Resources["NowPlayingBottomInset"] = new Thickness(0, 0, 0, TabBar.Bounds.Height + TabBar.Margin.Bottom + NowPlayingTabBarClearance);

    private IInsetsManager? _insets;

    // The phone would lay this view out inside the safe area, leaving a strip
    // between the tab bar and the bottom of the screen. So the view takes the
    // whole screen and applies the insets itself: top, left and right as its
    // own Padding, the bottom one inside the tab bar, whose background then
    // runs down behind the home indicator.
    //
    // The sheets span every row and run down behind the home indicator too;
    // each keeps its content clear of the inset itself (SheetBottomInset).
    //
    // The top level is painted with the screens' own brush as well, for any
    // platform that shows it despite the preference (a status bar strip).
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        topLevel[!TopLevel.BackgroundProperty] = new DynamicResourceExtension("AppBackgroundBrush");

        _insets = topLevel.InsetsManager;
        if (_insets is null)
            return;

        TopLevel.SetAutoSafeAreaPadding(this, false);
        _insets.DisplayEdgeToEdgePreference = true;
        _insets.SafeAreaChanged += OnSafeAreaChanged;
        ApplySafeArea(_insets.SafeAreaPadding);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_insets is not null)
        {
            _insets.SafeAreaChanged -= OnSafeAreaChanged;
            _insets = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e) => ApplySafeArea(e.SafeAreaPadding);

    private void ApplySafeArea(Thickness safeArea)
    {
        Padding = new Thickness(safeArea.Left, safeArea.Top, safeArea.Right, 0);
        // Not the whole bottom inset: the home indicator is a thin line at the
        // very bottom of that inset, so the oval can come most of the way down
        // into it before it crowds it. On a phone with no inset at all it
        // still floats clear of the edge.
        // 12 to each side, the mini player's own margin: the two ovals are the
        // same length, which takes the same margin as well as the same star
        // columns - see MobileMainView.axaml's comment on either of them.
        TabBar.Margin = new Thickness(12, 0, 12, Math.Max(TabBarMinimumBottomMargin, safeArea.Bottom - TabBarInsetOverlap));
        UpdateNowPlayingInset();

        // Every sheet's background runs down to the very bottom edge, under
        // the home indicator, and only what is on it keeps clear of the inset -
        // SheetBottomInset, which a card puts on its content and a whole-screen
        // sheet (Settings, Track Info) takes as its own Padding. Now Playing
        // takes NowPlayingBottomInset instead, the tab oval over it.
        Resources["SheetBottomInset"] = new Thickness(0, 0, 0, safeArea.Bottom);
    }

    // Tapping the Search tab icon while already on the Search tab is a no-op
    // as far as SelectTabCommand/SelectedTab's setter are concerned (it
    // guards against reassigning the same tab - see MobileMainViewModel), so
    // it never re-fires NavigationChanged (the sync point ScreenStackPanel's
    // own auto-focus runs off - see SyncToCurrentFrame). Handled here
    // instead, directly off the button's own Click - covers tapping the icon
    // again after scrolling the results (which blurs the box) or after
    // dismissing the keyboard, to bring focus (and the keyboard) straight
    // back rather than requiring a tap on the box itself.
    private void SearchTab_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MobileMainViewModel { SelectedTab: MobileTab.Search })
            ScreenStack.FocusSearchBoxIfShowing();
    }

    // Plays the same slide-off animation an interactive swipe-back gesture
    // does, rather than calling BackCommand directly and cutting straight to
    // the destination screen with no transition - see
    // ScreenStackPanel.AnimateGoBack.
    private void BackButton_Click(object? sender, RoutedEventArgs e) => ScreenStack.AnimateGoBack();
}
