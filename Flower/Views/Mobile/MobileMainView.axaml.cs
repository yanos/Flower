using System;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile;

public partial class MobileMainView : UserControl
{
    public MobileMainView()
    {
        InitializeComponent();
    }

    // Auto-focuses whichever of the two search boxes (see MobileMainView.axaml's
    // own comment on why there are two - SearchBox for the Songs tab's own
    // toggleable box over Main.FilterText, SearchTabBox for the dedicated
    // Search tab's always-visible one over SearchQuery) just became visible,
    // so tapping either one drops the keyboard straight in rather than
    // requiring a second tap on the box itself. Posted rather than called
    // inline - IsVisible's own binding update from the same PropertyChanged
    // notification hasn't necessarily been applied yet at this point, and
    // Avalonia won't focus a control that's still invisible.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MobileMainViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MobileMainViewModel.IsShowingSongsSearchBox) && vm.IsShowingSongsSearchBox)
                    Dispatcher.UIThread.Post(() => SearchBox.Focus());
                if (args.PropertyName == nameof(MobileMainViewModel.IsShowingSearchTabBox) && vm.IsShowingSearchTabBox)
                    Dispatcher.UIThread.Post(() => SearchTabBox.Focus());
            };
        }
    }

    // Tapping the Search tab icon while already on the Search tab is a no-op
    // as far as SelectTabCommand/SelectedTab's setter are concerned (it
    // guards against reassigning the same tab - see MobileMainViewModel), so
    // it never re-fires the IsShowingSearchTabBox PropertyChanged that the
    // constructor's subscription above focuses off of. Handled here instead,
    // directly off the button's own Click - covers tapping the icon again
    // after scrolling the results (which blurs the box) or after dismissing
    // the keyboard, to bring focus (and the keyboard) straight back rather
    // than requiring a tap on the box itself.
    private void SearchTab_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MobileMainViewModel { SelectedTab: MobileTab.Search })
            Dispatcher.UIThread.Post(() => SearchTabBox.Focus());
    }
}
