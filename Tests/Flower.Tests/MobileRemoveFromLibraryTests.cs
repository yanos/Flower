using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests;

// The phone's "Remove from Library": offered from a song's menu, confirmed
// through its own sheet with the delete-files choice off by default. What the
// removal then does across devices is SyncScenarioTests'.
[Collection("PlatformDataDirectory")]
public class MobileRemoveFromLibraryTests : PinnedDataDirectory
{
    private static MainViewModelHarness.MobileParts Build(Library library)
    {
        var settings = new AppSettings();
        var parts = MainViewModelHarness.BuildParts(library, new MainPlaylist(new List<Track>()), settings);
        var removal = new LibraryRemovalService(library, settings, resolver: null, credentials: null,
            NullLogger<LibraryRemovalService>.Instance);
        MainViewModelHarness.UseSuiteTabs(parts);
        var mobile = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying,
            NullLogger<MobileMainViewModel>.Instance, removal);
        // The rows are built off the UI thread; one pump is not enough on a
        // loaded runner.
        UiWait.Settle(() => mobile.Main.Rows.Count == library.Tracks.Count, "the song list never filled in");
        return new MainViewModelHarness.MobileParts(mobile, parts);
    }

    private static Track Mine() => new()
    {
        Title = "Mine", Artists = "Me", Album = "Demos", Duration = TimeSpan.FromSeconds(90), Path = "/phone/Mine.m4a",
    };

    [AvaloniaFact]
    public void Removing_a_song_asks_first_with_the_files_left_alone_by_default_and_then_removes_it()
    {
        var library = new Library([Mine()]);
        using var scope = Build(library);
        scope.Mobile.OpenTrackActionsCommand.Execute(scope.Mobile.Main.Rows.Single());

        Assert.True(scope.Mobile.CanRemoveActionTargetFromLibrary);
        scope.Mobile.RemoveFromLibraryCommand.Execute(null);

        Assert.True(scope.Mobile.IsShowingConfirmRemoveFromLibrary);
        Assert.True(scope.Mobile.OffersRemoveFromLibraryDeleteFiles);
        Assert.False(scope.Mobile.RemoveFromLibraryDeleteFiles);
        Assert.Single(library.Tracks);

        scope.Mobile.ConfirmRemoveFromLibraryCommand.Execute(null);
        UiWait.Settle(() => library.Tracks.Count == 0 && !scope.Mobile.IsShowingConfirmRemoveFromLibrary, "the song was never removed");

        Assert.Empty(library.Tracks);
        Assert.False(scope.Mobile.IsShowingConfirmRemoveFromLibrary);
    }

    [AvaloniaFact]
    public void Cancelling_keeps_the_song()
    {
        var library = new Library([Mine()]);
        using var scope = Build(library);
        scope.Mobile.OpenTrackActionsCommand.Execute(scope.Mobile.Main.Rows.Single());
        scope.Mobile.RemoveFromLibraryCommand.Execute(null);

        scope.Mobile.CancelRemoveFromLibraryCommand.Execute(null);

        Assert.Single(library.Tracks);
        Assert.False(scope.Mobile.IsShowingConfirmRemoveFromLibrary);
    }

    // ── From a screen's header menu ───────────────────────────────────────

    private static Track Song(string title, string album, string? path) => new()
    {
        Title = title, Artists = "Me", Album = album, Duration = TimeSpan.FromSeconds(90), Path = path,
    };

    private static List<string> MenuLabels(MobileMainViewModel mobile) =>
        mobile.BuildScreenMenu(mobile.CurrentFrame).SelectMany(s => s.Entries).Select(e => e.Label).ToList();

    private static ScreenMenuEntry MenuEntry(MobileMainViewModel mobile, string label) =>
        mobile.BuildScreenMenu(mobile.CurrentFrame).SelectMany(s => s.Entries).Single(e => e.Label == label);

    [AvaloniaFact]
    public void A_screens_menu_removes_what_it_shows_from_the_library_after_asking()
    {
        var library = new Library([Song("One", "Demos", "/phone/One.m4a"), Song("Two", "Demos", "/phone/Two.m4a")]);
        using var scope = Build(library);
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Songs));

        var entry = MenuEntry(mobile, "Remove from Library");
        Assert.True(entry.IsDestructive);
        entry.Invoke();

        Assert.True(mobile.IsShowingConfirmRemoveFromLibrary);
        Assert.Contains("2 songs", mobile.RemoveFromLibraryTitle);
        Assert.Equal(2, library.Tracks.Count);

        mobile.ConfirmRemoveFromLibraryCommand.Execute(null);
        UiWait.Settle(() => library.Tracks.Count == 0, "the songs were never removed");
    }

    // Once anything on screen has a file on this phone - and it deletes those
    // files only, confirmed by the same sheet as an album's.
    [AvaloniaFact]
    public void A_screens_menu_deletes_the_local_files_it_shows_once_there_are_any()
    {
        var library = new Library([Song("Here", "Demos", "/phone/Here.m4a"), Song("There", "Demos", null)]);
        using var scope = Build(library);
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Songs));

        MenuEntry(mobile, "Delete Local Files").Invoke();

        Assert.True(mobile.IsShowingConfirmDeleteFile);
        Assert.Equal("Delete 1 local file of \"Songs\"?", mobile.ConfirmDeleteFileTitle);
    }

    [AvaloniaFact]
    public void With_nothing_downloaded_there_is_nothing_to_delete()
    {
        var library = new Library([Song("There", "Demos", null)]);
        var settings = new AppSettings();
        var parts = MainViewModelHarness.BuildParts(library, new MainPlaylist(new List<Track>()), settings);
        MainViewModelHarness.UseSuiteTabs(parts);
        using var scope = new MainViewModelHarness.MobileParts(new MobileMainViewModel(parts.Main, parts.PlaylistControl,
            parts.CurrentlyPlaying, NullLogger<MobileMainViewModel>.Instance), parts);
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        UiWait.Settle(() => scope.Mobile.Main.Rows.Count == 1, "the song list never filled in");

        Assert.DoesNotContain("Delete Local Files", MenuLabels(scope.Mobile));
    }

    // On a playlist it would read as deleting the playlist, and take every
    // song in it out of the library instead.
    [AvaloniaFact]
    public void A_playlists_menu_does_not_offer_to_remove_its_songs_from_the_library()
    {
        var library = new Library([Song("One", "Demos", "/phone/One.m4a")]);
        library.AddPlaylist(new Playlist("Mix", library.Tracks.ToList()));
        using var scope = Build(library);
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Playlists));
        UiWait.Settle(() => mobile.PlaylistPickerItems.Any(i => i.Playlist != null), "the playlist never showed");

        // The menu is over songs - it offers the rest - just not this one.
        Assert.Contains("Delete Local Files", MenuLabels(mobile));
        Assert.DoesNotContain("Remove from Library", MenuLabels(mobile));
    }
}
