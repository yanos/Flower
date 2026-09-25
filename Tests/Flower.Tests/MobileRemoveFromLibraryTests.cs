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
}
