using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests.ViewModels;

// A playlist can hold rows this device cannot play. Saying so is what keeps a
// shorter list from looking like lost music.
[Collection("PlatformDataDirectory")]
public class UnavailableSongsLineTests : PinnedDataDirectory
{
    private static readonly PlaylistSyncTrackDto Away = new("Away", "Artist", "Album", 100, "not-here");

    private static (Library Library, Playlist Playlist) LibraryMissing(int songs)
    {
        var here = new Track { Title = "Here", Path = "/music/here.mp3" };
        var entries = new List<PlaylistEntry> { PlaylistEntry.Of(here) };
        entries.AddRange(Enumerable.Range(0, songs).Select(_ => new PlaylistEntry(Guid.NewGuid(), null, Song: Away)));
        var playlist = new Playlist(Guid.NewGuid(), "Mix", [], DateTimeOffset.UtcNow, entries: entries);
        var library = new Library([here]);
        library.AddPlaylist(playlist);
        return (library, playlist);
    }

    [AvaloniaFact]
    public void The_desktop_page_counts_the_songs_it_cannot_play()
    {
        var (library, playlist) = LibraryMissing(2);
        using var parts = MainViewModelHarness.Build(library, new MainPlaylist([]));

        parts.Main.SelectedSidebarItem = parts.Main.SidebarItems.Single(i => i.Playlist?.Id == playlist.Id);

        Assert.Contains("2 songs unavailable", parts.Main.PageSubtitle);
    }

    [AvaloniaFact]
    public void A_playlist_with_every_song_here_says_nothing()
    {
        var (library, playlist) = LibraryMissing(0);
        using var parts = MainViewModelHarness.Build(library, new MainPlaylist([]));

        parts.Main.SelectedSidebarItem = parts.Main.SidebarItems.Single(i => i.Playlist?.Id == playlist.Id);

        Assert.DoesNotContain("unavailable", parts.Main.PageSubtitle);
    }

    [AvaloniaFact]
    public void A_new_device_says_nothing_until_its_catalog_has_arrived()
    {
        var (library, playlist) = LibraryMissing(1);
        using var parts = MainViewModelHarness.Build(
            library, new MainPlaylist([]), new AppSettings { PairedServerFingerprint = "server-fp" });
        parts.Main.SelectedSidebarItem = parts.Main.SidebarItems.Single(i => i.Playlist?.Id == playlist.Id);
        Assert.DoesNotContain("unavailable", parts.Main.PageSubtitle);

        library.UpdateTracks(
        [
            .. library.Tracks,
            new Track { Title = "From Server", OriginDeviceFingerprint = "server-fp", OriginTrackId = "t1" },
        ]);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("1 song unavailable", parts.Main.PageSubtitle);
    }

    [AvaloniaFact]
    public void The_phone_header_counts_them_too()
    {
        var (library, playlist) = LibraryMissing(1);
        using var parts = MainViewModelHarness.BuildMobile(library, new MainPlaylist([]));
        var vm = parts.Mobile;
        vm.SelectTabCommand.Execute(nameof(MobileTab.Playlists));
        UiWait.Settle(() => vm.PlaylistPickerItems.Any(i => i.Playlist?.Id == playlist.Id), "never reached the picker");

        vm.SelectPlaylistCommand.Execute(vm.PlaylistPickerItems.First(i => i.Playlist?.Id == playlist.Id));
        UiWait.Settle(() => vm.CurrentPlaylistHeader != null, "never drilled in");

        Assert.Equal("1 song unavailable", vm.CurrentPlaylistHeader!.Note);
    }
}
