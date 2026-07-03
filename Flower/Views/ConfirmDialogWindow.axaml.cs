using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Flower.Views;

// Generic Yes/No confirmation dialog. Cancel is the default/Escape action
// rather than Confirm - appropriate for the destructive confirmations (e.g.
// deleting a playlist) this is meant for, where Enter shouldn't be a shortcut
// straight into the irreversible action.
public partial class ConfirmDialogWindow : Window
{
    public ConfirmDialogWindow(string title, string message, string confirmText)
    {
        InitializeComponent();
        Title = title;
        HeadlineText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    public static Task<bool> ShowAsync(Window owner, string title, string message, string confirmText)
        => new ConfirmDialogWindow(title, message, confirmText).ShowDialog<bool>(owner);

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e) => Close(true);
}
