using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile;

public partial class MobileMainView : UserControl
{
    public MobileMainView()
    {
        InitializeComponent();
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
