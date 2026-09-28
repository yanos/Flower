using Avalonia.Controls;

using Flower.Services;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile.Screens;

public partial class ArtistPickerScreenView : UserControl
{
    public ArtistPickerScreenView()
    {
        InitializeComponent();

        // The artists are sorted on the raw name (LibraryBrowserViewModel.
        // RebuildSubListItems), hence no skipping punctuation.
        IndexBar.IndexOfLetter = letter => DataContext is MobileMainViewModel vm
            ? AlphabetIndex.FirstIndexFor(vm.ArtistPickerItems, r => AlphabetIndex.LetterOf(r.Name, skipPunctuation: false), letter,
                descending: vm.ArtistPickerRunsZToA)
            : -1;
    }
}
