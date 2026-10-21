using CommunityToolkit.Mvvm.ComponentModel;

using Material.Icons;

namespace Flower.ViewModels;

// One of Home's shelves in Settings, on either head: its name, how much it
// holds, and - by the list these are in (MainViewModel.HomeShelfSettings) -
// where it comes on the page. The amount is MainViewModel's own setting for
// that shelf, read and written through, so it is applied and saved the way
// it always was.
public sealed class HomeShelfSettingRow(HomeShelf shelf, MainViewModel main) : ObservableObject
{
    public HomeShelf Shelf { get; } = shelf;

    public string Title => HomeShelfOrder.Title(Shelf);

    public MaterialIconKind Icon => HomeShelfOrder.Icon(Shelf);

    // Recently Added is months back; the other two are albums.
    public string Unit => Shelf == HomeShelf.RecentlyAdded ? "months" : "albums";

    public int Maximum => Shelf == HomeShelf.RecentlyAdded ? HomeViewModel.MaxRecentlyAddedMonths : HomeViewModel.MaxAlbumsPerShelf;

    public int Value
    {
        get => Shelf switch
        {
            HomeShelf.ContinuePlaying => main.HomeContinuePlayingLimit,
            HomeShelf.RecentlyPlayed => main.HomeRecentlyPlayedLimit,
            _ => main.HomeRecentlyAddedMonths,
        };
        set
        {
            switch (Shelf)
            {
                case HomeShelf.ContinuePlaying:
                    main.HomeContinuePlayingLimit = value;
                    break;
                case HomeShelf.RecentlyPlayed:
                    main.HomeRecentlyPlayedLimit = value;
                    break;
                default:
                    main.HomeRecentlyAddedMonths = value;
                    break;
            }
            OnPropertyChanged();
        }
    }
}
