using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests.ViewModels.Mobile;

// The phone's Add to Playlist sheet: the playlists last changed at its top,
// and a question before a song goes into a playlist that holds it already.
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

    private static SidebarItem Row(MobileMainViewModel vm, string name) =>
        vm.PlaylistPickerItems.Single(i => i.Playlist?.Name == name);

    // A playlist's menu, Add to Playlist, then a playlist picked in the sheet:
    // every song of the first going into the second.
    private static void AddPlaylistTo(MobileMainViewModel vm, string from, string into)
    {
        vm.OpenPlaylistActionsCommand.Execute(Row(vm, from));
        vm.AddAlbumActionTargetToPlaylistCommand.Execute(null);
        vm.AddTrackToPlaylistCommand.Execute(Row(vm, into));
    }

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

    [AvaloniaFact]
    public async Task A_song_already_in_the_playlist_is_asked_about_and_a_no_adds_nothing()
    {
        var song = T("Tassels");
        var library = new Library([song]);
        library.AddPlaylist(P("From", [song], daysAgo: 1));
        library.AddPlaylist(P("Mix", [song], daysAgo: 1));
        var vm = Build(library);

        AddPlaylistTo(vm, "From", "Mix");
        await WaitFor(() => vm.ActiveSheet == MobileSheet.ConfirmAddDuplicates);
        Assert.Equal(MobileSheet.ConfirmAddDuplicates, vm.ActiveSheet);

        vm.DeclineAddDuplicatesCommand.Execute(null);
        await WaitFor(() => vm.ActiveSheet == MobileSheet.None);

        Assert.Equal(MobileSheet.None, vm.ActiveSheet);
        Assert.Single(library.Playlists.Single(p => p.Name == "Mix").Tracks);
    }

    [AvaloniaFact]
    public async Task A_yes_adds_the_song_again()
    {
        var song = T("Tassels");
        var library = new Library([song]);
        library.AddPlaylist(P("From", [song], daysAgo: 1));
        library.AddPlaylist(P("Mix", [song], daysAgo: 1));
        var vm = Build(library);

        AddPlaylistTo(vm, "From", "Mix");
        await WaitFor(() => vm.ActiveSheet == MobileSheet.ConfirmAddDuplicates);
        vm.ConfirmAddDuplicatesCommand.Execute(null);
        await WaitFor(() => library.Playlists.Single(p => p.Name == "Mix").Tracks.Count == 2);

        Assert.Equal(2, library.Playlists.Single(p => p.Name == "Mix").Tracks.Count);
        Assert.Equal(MobileSheet.None, vm.ActiveSheet);
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 2000)
    {
        for (var waited = 0; waited < timeoutMs && !condition(); waited += 20)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
