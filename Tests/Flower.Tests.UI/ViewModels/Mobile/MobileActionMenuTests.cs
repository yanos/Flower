using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Flower.Models;
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

    // A phone has nowhere to show a file, so the song sheet is the desktop's
    // menu without Locate File.
    [AvaloniaFact]
    public async Task The_song_sheet_offers_no_Locate_File()
    {
        var tracks = Album();
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        var vm = parts.Mobile;
        await parts.Parts.Main.Browser.RebuildRowsImmediatelyAsync();

        vm.OpenTrackActionsCommand.Execute(vm.Main.Rows.First(r => r.Track == tracks[0]));

        Assert.Contains(vm.SongActions, e => e.Action == MenuAction.GetInfo);
        Assert.DoesNotContain(vm.SongActions, e => e.Action == MenuAction.LocateFile);
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
        Assert.Equal("Avant Hard", vm.Main.SelectedSubItem);

        vm.OpenTrackActionsCommand.Execute(vm.AlbumDetailRows.First());
        Assert.DoesNotContain(vm.SongActions, e => e.Action == MenuAction.GoToAlbum);
        Assert.Contains(vm.SongActions, e => e.Action == MenuAction.GoToArtist);
    }

    [AvaloniaFact]
    public void The_album_sheet_goes_to_the_albums_artist()
    {
        var tracks = Album();
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        var vm = parts.Mobile;
        vm.OpenAlbumActionsCommand.Execute(new AlbumTileViewModel
        {
            Name = "Avant Hard", Artist = "Add N To (X)", Tracks = tracks, RepresentativeTrack = tracks[0],
        });

        vm.RunAlbumActionCommand.Execute(vm.AlbumActions.Single(e => e.Action == MenuAction.GoToArtist));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(MobileSheet.None, vm.ActiveSheet);
        Assert.True(vm.IsShowingArtistAlbumGrid);
        Assert.Equal("Add N To (X)", vm.CurrentFrame.SelectedArtistName);
    }
}
