using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.ViewModels.Mobile;

// UF-7: a phone used to keep its own version of a conflict without asking,
// and push it over the other device's edits.
[Collection("PlatformDataDirectory")]
public class MobilePlaylistConflictTests : PinnedDataDirectory
{
    private static MainViewModelHarness.MobileParts Build()
    {
        var parts = MainViewModelHarness.BuildParts(new Library(new List<Track>()), new MainPlaylist(new List<Track>()));
        MainViewModelHarness.UseSuiteTabs(parts);
        var mobile = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying, NullLogger<MobileMainViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        return new MainViewModelHarness.MobileParts(mobile, parts);
    }

    private static PlaylistConflictEventArgs Conflict() => new()
    {
        Local = new Playlist("Mix", new List<Track>()),
        Remote = new PlaylistSyncPlaylistDto(Guid.NewGuid(), "Mix", DateTimeOffset.UtcNow, []),
        RemoteAlias = "Attic",
        Resolution = new TaskCompletionSource<PlaylistConflictChoice>(),
    };

    [AvaloniaFact]
    public void A_conflict_asks_and_waits_for_the_answer()
    {
        using var scope = Build();
        var conflict = Conflict();

        scope.Mobile.AskAboutPlaylistConflict(conflict);

        Assert.True(scope.Mobile.IsShowingPlaylistConflict);
        Assert.False(conflict.Resolution.Task.IsCompleted);

        scope.Mobile.KeepRemotePlaylistCommand.Execute(null);

        Assert.Equal(PlaylistConflictChoice.KeepRemote, conflict.Resolution.Task.Result);
        Assert.False(scope.Mobile.IsShowingPlaylistConflict);
    }

    // UF-21: deleted on the server, with nothing on this phone to say whether
    // its copy holds an edit. The sheet asks; deleting it here is the answer
    // the session then carries out.
    [AvaloniaFact]
    public void A_playlist_deleted_on_the_server_is_asked_about_and_can_be_deleted_here()
    {
        using var scope = Build();
        var local = new Playlist("Mix", new List<Track>());
        var deletion = new PlaylistConflictEventArgs
        {
            Local = local,
            Deletion = new PlaylistDeletionDto(local.Id, 20),
            RemoteAlias = "Attic",
            Resolution = new TaskCompletionSource<PlaylistConflictChoice>(),
        };

        scope.Mobile.AskAboutPlaylistConflict(deletion);
        Assert.True(scope.Mobile.IsShowingPlaylistConflict);

        scope.Mobile.KeepRemotePlaylistCommand.Execute(null);

        Assert.Equal(PlaylistConflictChoice.KeepRemote, deletion.Resolution.Task.Result);
    }

    [AvaloniaFact]
    public void Closing_the_sheet_decides_nothing()
    {
        using var scope = Build();
        var conflict = Conflict();
        scope.Mobile.AskAboutPlaylistConflict(conflict);

        scope.Mobile.CloseSheetCommand.Execute(null);

        Assert.Equal(PlaylistConflictChoice.Later, conflict.Resolution.Task.Result);
    }

    [AvaloniaFact]
    public void A_conflict_does_not_take_over_a_sheet_in_use()
    {
        using var scope = Build();
        scope.Mobile.OpenSettingsCommand.Execute(null);
        var conflict = Conflict();

        scope.Mobile.AskAboutPlaylistConflict(conflict);

        Assert.True(scope.Mobile.IsShowingSettings);
        Assert.Equal(PlaylistConflictChoice.NotAsked, conflict.Resolution.Task.Result);
    }
}
