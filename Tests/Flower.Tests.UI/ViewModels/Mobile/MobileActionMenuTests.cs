using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests.ViewModels.Mobile;

// The phone's action sheets list ActionMenus' entries and run each through
// the command the phone already had for it.
[Collection("PlatformDataDirectory")]
public class MobileActionMenuTests : PinnedDataDirectory
{
    private static List<Track> Album() =>
    [
        new() { Title = "Skills", Album = "Avant Hard", Artists = "Add N To (X)", TrackNumber = 1, Path = "/m/1.mp3" },
        new() { Title = "Fyuz", Album = "Avant Hard", Artists = "Add N To (X)", TrackNumber = 2, Path = "/m/2.mp3" },
    ];

    [AvaloniaFact]
    public void Picking_an_entry_on_the_album_sheet_runs_it()
    {
        var tracks = Album();
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        var vm = parts.Mobile;
        vm.OpenAlbumActionsCommand.Execute(new AlbumTileViewModel
        {
            Name = "Avant Hard", Artist = "Add N To (X)", Tracks = tracks, RepresentativeTrack = tracks[0],
        });

        var playNext = vm.AlbumActions.Single(e => e.Action == MenuAction.PlayNext);
        vm.RunAlbumActionCommand.Execute(playNext);

        Assert.Equal(MobileSheet.None, vm.ActiveSheet);
        Assert.Equal(tracks, parts.Parts.PlaylistControl.CurrentPlaylist.Tracks.Take(2));
    }

    // Remove from Continue Playing is the shelf's own: on the album's menu
    // when it is raised there, not when the same album is long-pressed in
    // the grid or on another shelf.
    [AvaloniaFact]
    public void Only_the_continue_playing_shelf_offers_to_take_an_album_off_it()
    {
        var tracks = Album();
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        var vm = parts.Mobile;
        parts.Parts.PlaylistControl.AlbumProgress.MergeRemote(
            [new AlbumProgressEntry(AlbumProgressTracker.AlbumIdOf(tracks[0])!, tracks[1].Id, TimeSpan.Zero, DateTimeOffset.UtcNow)],
            []);
        vm.Main.Home.Rebuild();
        var item = vm.Main.Home.ContinuePlaying.Single();

        vm.OpenAlbumActionsCommand.Execute(item.Tile);
        Assert.DoesNotContain(vm.AlbumActions, e => e.Action == MenuAction.ForgetProgress);

        vm.OpenAlbumActionsCommand.Execute(item);
        vm.RunAlbumActionCommand.Execute(vm.AlbumActions.Single(e => e.Action == MenuAction.ForgetProgress));
        Assert.Empty(parts.Parts.PlaylistControl.AlbumProgress.Entries);
    }

    // Resume on the shelf's sheet picks the album up where it was left, as
    // tapping its cover does, not from its first song.
    [AvaloniaFact]
    public void Resume_on_the_continue_playing_sheet_picks_the_album_up_where_it_was_left()
    {
        var tracks = Album();
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist([]));
        var vm = parts.Mobile;
        parts.Parts.PlaylistControl.AlbumProgress.MergeRemote(
            [new AlbumProgressEntry(AlbumProgressTracker.AlbumIdOf(tracks[0])!, tracks[1].Id, TimeSpan.Zero, DateTimeOffset.UtcNow)],
            []);
        vm.Main.Home.Rebuild();

        vm.OpenAlbumActionsCommand.Execute(vm.Main.Home.ContinuePlaying.Single());
        vm.RunAlbumActionCommand.Execute(vm.AlbumActions.Single(e => e.Action == MenuAction.Resume));

        Assert.Equal(MobileSheet.None, vm.ActiveSheet);
        Assert.Equal(tracks[1], parts.Parts.PlaylistControl.CurrentlyPlayingTrack);
    }

    [AvaloniaFact]
    public void Go_to_Album_on_the_continue_playing_sheet_opens_the_album()
    {
        var tracks = Album();
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        var vm = parts.Mobile;
        parts.Parts.PlaylistControl.AlbumProgress.MergeRemote(
            [new AlbumProgressEntry(AlbumProgressTracker.AlbumIdOf(tracks[0])!, tracks[1].Id, TimeSpan.Zero, DateTimeOffset.UtcNow)],
            []);
        vm.Main.Home.Rebuild();

        vm.OpenAlbumActionsCommand.Execute(vm.Main.Home.ContinuePlaying.Single());
        vm.RunAlbumActionCommand.Execute(vm.AlbumActions.Single(e => e.Action == MenuAction.GoToAlbum));
        MainViewModelHarness.WaitForTheDrillIn(vm, "Avant Hard");

        Assert.Equal(MobileSheet.None, vm.ActiveSheet);
        Assert.Equal("Avant Hard", vm.CurrentAlbumHeader?.Name);
    }

    // The desktop's Go to Album and Go to Artist, on the song sheet: where the
    // song is from, first, and not where the sheet was raised already.
    [AvaloniaFact]
    public async Task The_song_sheet_goes_to_the_songs_album()
    {
        var tracks = Album();
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        var vm = parts.Mobile;
        await parts.Parts.Main.Browser.RebuildRowsImmediatelyAsync();

        vm.OpenTrackActionsCommand.Execute(vm.Main.Rows.First(r => r.Track == tracks[0]));
        Assert.Equal([MenuAction.GoToAlbum, MenuAction.GoToArtist], vm.SongActions.Take(2).Select(e => e.Action));

        vm.RunSongActionCommand.Execute(vm.SongActions.Single(e => e.Action == MenuAction.GoToAlbum));
        MainViewModelHarness.WaitForTheDrillIn(vm, "Avant Hard");

        Assert.Equal(MobileSheet.None, vm.ActiveSheet);
        Assert.Equal("Avant Hard", vm.CurrentAlbumHeader?.Name);

        vm.OpenTrackActionsCommand.Execute(vm.AlbumDetailRows.First());
        Assert.DoesNotContain(vm.SongActions, e => e.Action == MenuAction.GoToAlbum);
        Assert.Contains(vm.SongActions, e => e.Action == MenuAction.GoToArtist);
    }
}
