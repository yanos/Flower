using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels.Home;

// The order of Home's shelves as settings.json keeps it: every shelf is always
// somewhere in it, whatever the file says.
public class HomeShelfOrderTests
{
    [Fact]
    public void A_saved_order_is_cleaned_of_names_that_are_not_shelves_and_repeats()
    {
        var order = HomeShelfOrder.Parse(["RecentlyAdded", "Favourites", "RecentlyAdded", "ContinuePlaying"]);

        Assert.Equal(new[] { HomeShelf.RecentlyAdded, HomeShelf.ContinuePlaying, HomeShelf.RecentlyPlayed }, order);
    }

    [Fact]
    public void Nothing_saved_is_the_default_order()
    {
        Assert.Equal(HomeShelfOrder.Default, HomeShelfOrder.Parse(null));
    }
}
