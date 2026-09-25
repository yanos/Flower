using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.Services;

namespace Flower.Views;

// Generic Yes/No confirmation dialog. Cancel is the default/Escape action
// rather than Confirm - appropriate for the destructive confirmations (e.g.
// deleting a playlist) this is meant for, where Enter shouldn't be a shortcut
// straight into the irreversible action.
public partial class ConfirmDialogWindow : Window
{
    // Satisfies Avalonia's runtime-XAML-loader/previewer check (AVLN3001) -
    // never called directly; the real constructor below is what's actually used.
    public ConfirmDialogWindow() => InitializeComponent();

    public ConfirmDialogWindow(string title, string message, string confirmText)
    {
        InitializeComponent();
        Title = title;
        HeadlineText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        NativeMenuHelper.InheritFromMainWindow(this);
    }

    public static Task<bool> ShowAsync(Window owner, string title, string message, string confirmText)
        => new ConfirmDialogWindow(title, message, confirmText).ShowDialog<bool>(owner);

    // The same confirmation with one checkbox under the message, off by
    // default. Null when cancelled, otherwise whether the box was ticked.
    public static async Task<bool?> ShowWithOptionAsync(
        Window owner, string title, string message, string confirmText, string optionText)
    {
        var dialog = new ConfirmDialogWindow(title, message, confirmText);
        dialog.OptionCheckBox.Content = optionText;
        dialog.OptionCheckBox.IsVisible = true;
        return await dialog.ShowDialog<bool>(owner) ? dialog.OptionCheckBox.IsChecked == true : null;
    }

    // Something to tell rather than ask: one button, no Cancel.
    public static Task ShowMessageAsync(Window owner, string title, string message)
    {
        var dialog = new ConfirmDialogWindow(title, message, "OK");
        dialog.CancelButton.IsVisible = false;
        dialog.ConfirmButton.IsDefault = true;
        dialog.ConfirmButton.IsCancel = true;
        return dialog.ShowDialog<bool>(owner);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e) => Close(true);
}
