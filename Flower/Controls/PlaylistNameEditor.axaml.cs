using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Flower.Controls;

/// <summary>
/// The name box a playlist screen's header shows while it is being renamed -
/// see the markup. The code-behind is what a binding cannot do: putting the
/// caret in the box as it appears, and deciding which gesture commits.
/// </summary>
public partial class PlaylistNameEditor : UserControl
{
    // Handed the SidebarItem being renamed: MobileMainViewModel's
    // CommitPlaylistRenameCommand, the picker row's own.
    public static readonly StyledProperty<ICommand?> CommitCommandProperty =
        AvaloniaProperty.Register<PlaylistNameEditor, ICommand?>(nameof(CommitCommand));

    public ICommand? CommitCommand
    {
        get => GetValue(CommitCommandProperty);
        set => SetValue(CommitCommandProperty, value);
    }

    public PlaylistNameEditor()
    {
        InitializeComponent();

        // Realized with the header long before the pencil is pressed, so it is
        // showing, not loading, that means "start typing". Posted rather than
        // taken here for the reason NewPlaylistEntry's is: at this point the
        // box has been told to show but not laid out, and refuses focus.
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty || !IsVisible)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                Box.Focus();
                Box.SelectAll();
            }, DispatcherPriority.Loaded);
        };
    }

    // Enter and Escape both end it, as on the picker's row: the name is bound
    // live, so there is nothing to put back.
    private void Box_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape))
            return;
        e.Handled = true;
        Commit();
    }

    private void Box_LostFocus(object? sender, RoutedEventArgs e) => Commit();

    private void Commit() => CommitCommand?.Execute(DataContext);
}
