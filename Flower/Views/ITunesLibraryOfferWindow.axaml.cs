using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.Persistence;
using Flower.Services;

namespace Flower.Views;

// The first-run question: a Music.app library was found, should Flower use it,
// and should it bring play counts and Date Added across each launch. Shown
// once, over the main window, by App.axaml.cs - see ITunesLibraryOffer for
// when it is owed and what each answer does to the settings.
//
// Add is the default here, unlike ConfirmDialogWindow's Cancel: nothing about
// saying yes is destructive, and it is what Enter should mean on a question a
// new install asks before it has any music to show.
public partial class ITunesLibraryOfferWindow : Window
{
    // Satisfies Avalonia's runtime-XAML-loader/previewer check (AVLN3001) -
    // never called directly; the real constructor below is what's actually used.
    public ITunesLibraryOfferWindow() => InitializeComponent();

    public ITunesLibraryOfferWindow(string folder)
    {
        InitializeComponent();
        FolderText.Text = folder;
        ToolTip.SetTip(FolderText, folder);
        NativeMenuHelper.InheritFromMainWindow(this);
    }

    // Null when declined - by the button, Escape, or closing the window.
    public static Task<ITunesLibraryOfferAnswer?> ShowAsync(Window owner, string folder)
        => new ITunesLibraryOfferWindow(folder).ShowDialog<ITunesLibraryOfferAnswer?>(owner);

    private void DeclineButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void AddButton_Click(object? sender, RoutedEventArgs e) =>
        Close(new ITunesLibraryOfferAnswer(
            SyncPlayCount: SyncPlayCountCheckBox.IsChecked == true,
            SyncDateAdded: SyncDateAddedCheckBox.IsChecked == true));
}
