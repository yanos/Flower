using System;
using System.Windows.Input;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using Flower.ViewModels.Mobile;

namespace Flower.Controls;

// See the XAML's own comment.
public partial class ScreenTitle : UserControl
{
    public ScreenTitle()
    {
        InitializeComponent();
    }

    // The screen's, read off the slot hosting this title, as the markup's
    // bindings read everything else.
    private MobileMainViewModel? ViewModel =>
        this.FindAncestorOfType<ScreenSlot>()?.DataContext as MobileMainViewModel;

    // The card is a popup and does not inherit the slot's DataContext, so its
    // rows' IsVisible (smart and vibe playlists, where they can be made) would
    // have nothing to read.
    private void NewMenu_Opening(object? sender, EventArgs e) =>
        NewMenuItems.DataContext = ViewModel;

    // Closed first, so the row it opens (the name box) or the sheet it raises
    // (a smart or vibe playlist's editor) comes up over the screen rather than
    // under the menu - then what the row says to make.
    private void NewMenuRow_Click(object? sender, RoutedEventArgs e)
    {
        NewMenuButton.Flyout?.Hide();
        if (ViewModel is not { } vm || sender is not Button { Tag: string what })
            return;

        ICommand command = what switch
        {
            "smart" => vm.NewSmartPlaylistCommand,
            "vibe" => vm.NewVibePlaylistCommand,
            "folder" => vm.BeginCreateFolderCommand,
            _ => vm.BeginCreatePlaylistCommand,
        };
        command.Execute(null);
    }
}
