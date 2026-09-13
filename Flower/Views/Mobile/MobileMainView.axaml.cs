using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;

using Flower.Controls;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile;

public partial class MobileMainView : UserControl
{
    public MobileMainView()
    {
        InitializeComponent();
    }

    private const double TabBarInsetOverlap = 14;

    private IInsetsManager? _insets;

    // The phone would lay this view out inside the safe area, leaving a strip
    // between the tab bar and the bottom of the screen. So the view takes the
    // whole screen and applies the insets itself: top, left and right as its
    // own Padding, the bottom one inside the tab bar, whose background then
    // runs down behind the home indicator.
    //
    // The sheets span every row, so they get the bottom inset as a margin -
    // they sit exactly where they did when the platform padded everything.
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
        // very bottom of that inset, so the buttons can come most of the way
        // down into it before they crowd it.
        TabBar.Padding = new Thickness(0, 0, 0, Math.Max(0, safeArea.Bottom - TabBarInsetOverlap));

        foreach (var child in Root.Children)
        {
            if (child is SlidingSheet sheet)
                sheet.Margin = new Thickness(0, 0, 0, safeArea.Bottom);
        }
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
