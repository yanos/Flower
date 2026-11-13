using Flower.ViewModels.Mobile;

namespace Flower.Tests.ViewModels.Mobile;

public class MobileTabSettingsTests
{
    [Fact]
    public void A_saved_bar_is_cleaned_rather_than_trusted()
    {
        Assert.Equal(MobileTabs.Default, MobileTabs.Parse(null));
        Assert.Equal(MobileTabs.Default, MobileTabs.Parse(["NotATab"]));
        Assert.Equal(new[] { MobileTab.Songs, MobileTab.Queue },
            MobileTabs.Parse(["Songs", "Bogus", "Songs", "Queue"]));
        Assert.Equal(MobileTabs.Default, MobileTabs.Parse(MobileTabs.Default.Select(t => t.ToString())));
    }

    [Fact]
    public void A_saved_bar_longer_than_seven_is_cut_to_seven()
    {
        var saved = MobileTabs.All.Select(t => t.ToString()).ToList();

        Assert.Equal(MobileTabs.All.Take(MobileTabs.MaxShown), MobileTabs.Parse(saved));
    }

    [Fact]
    public void Toggling_tabs_keeps_between_one_and_the_maximum()
    {
        Assert.Equal(MobileTabs.Default, MobileTabs.Toggle(MobileTabs.Default, MobileTab.History));
        var tabs = MobileTabs.Toggle(MobileTabs.Default, MobileTab.Search);
        tabs = MobileTabs.Toggle(tabs, MobileTab.History);
        Assert.Equal(MobileTabs.MaxShown, tabs.Count);
        Assert.Equal(MobileTab.History, tabs[^1]);
        Assert.DoesNotContain(MobileTab.Search, tabs);
        Assert.Equal([MobileTab.Songs], MobileTabs.Toggle([MobileTab.Songs], MobileTab.Songs));
    }

    [Fact]
    public void Moving_a_tab_changes_its_position_and_leaves_hidden_tabs_alone()
    {
        MobileTab[] tabs = [MobileTab.Home, MobileTab.Songs, MobileTab.Albums];
        Assert.Equal([MobileTab.Albums, MobileTab.Home, MobileTab.Songs], MobileTabs.Move(tabs, MobileTab.Albums, 0));
        Assert.Equal([MobileTab.Songs, MobileTab.Albums, MobileTab.Home], MobileTabs.Move(tabs, MobileTab.Home, 2));
        Assert.Equal(tabs, MobileTabs.Move(tabs, MobileTab.Queue, 0));
    }
}
