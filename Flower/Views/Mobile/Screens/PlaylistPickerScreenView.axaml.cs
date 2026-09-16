using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile.Screens;

public partial class PlaylistPickerScreenView : UserControl
{
    public PlaylistPickerScreenView()
    {
        InitializeComponent();
    }

    private MobileMainViewModel? ViewModel => DataContext as MobileMainViewModel;

    // The box only exists while the row is being renamed, so it is realized
    // the moment editing begins and this fires once, on the way in - unlike
    // the desktop sidebar's, which hides and shows a box that was realized
    // long before (see MainView.BeginRename's own note about that).
    private void RenameBox_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        box.Focus();
        box.SelectAll();
    }

    // Enter and Escape are the same answer here as on the desktop sidebar:
    // the name is bound live, so there is nothing to put back, and both end
    // the edit. A phone mostly ends it the other way, by tapping elsewhere.
    private void RenameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape))
            return;
        e.Handled = true;
        Commit(sender);
    }

    private void RenameBox_LostFocus(object? sender, RoutedEventArgs e) => Commit(sender);

    private void Commit(object? sender)
    {
        if (sender is Control { DataContext: SidebarItem item })
            ViewModel?.CommitPlaylistRenameCommand.Execute(item);
    }
}
