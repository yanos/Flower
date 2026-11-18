using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests.ViewModels.Mobile;

// The phone's Add to Playlist sheet: the playlists last changed at its top.
[Collection("PlatformDataDirectory")]
public class AddToPlaylistSheetTests : PinnedDataDirectory
{
    public AddToPlaylistSheetTests() => TestIoc.EnsureConfigured();

    private static readonly DateTimeOffset Then = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static Track T(string title) => new()
    {
        Title = title,
        Album = "Syro",
        Artists = "Aphex Twin",
        Path = "/music/" + title + ".flac",
        Duration = TimeSpan.FromMinutes(4),
        DateAdded = DateTimeOffset.UtcNow,
    };

    private static Playlist P(string name, List<Track> tracks, int daysAgo) =>
        new(Guid.NewGuid(), name, tracks, Then.AddDays(-daysAgo));

    private MobileMainViewModel Build(Library library) =>
        Own(MainViewModelHarness.BuildMobile(library, new MainPlaylist(new List<Track>()))).Mobile;

    [AvaloniaFact]
    public void The_sheet_offers_the_two_playlists_changed_last_first()
    {
        var library = new Library([T("Tassels")]);
        library.AddPlaylist(P("Old", [], daysAgo: 30));
        library.AddPlaylist(P("Newest", [], daysAgo: 1));
        library.AddPlaylist(P("Middle", [], daysAgo: 10));
        // Changed last of all, but nothing can be added to it.
        var rules = new SmartPlaylistRules(
            MatchMode.All,
            [new SmartCondition(SmartField.Genre, SmartOperator.Is, new SmartValue.Text("Jazz"))],
            null,
            LiveUpdating: true);
        library.AddPlaylist(new Playlist(Guid.NewGuid(), "Rules", [], Then, rules: rules));
        var vm = Build(library);

        Assert.Equal(["Newest", "Middle"], vm.RecentPlaylistTargets.Select(i => i.Name));
    }

    // With two, both are at hand in the list already.
    [AvaloniaFact]
    public void Two_playlists_have_no_recent_section()
    {
        var library = new Library([T("Tassels")]);
        library.AddPlaylist(P("One", [], daysAgo: 1));
        library.AddPlaylist(P("Two", [], daysAgo: 2));
        var vm = Build(library);

        Assert.False(vm.HasRecentPlaylistTargets);
    }
}
