using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests.Playback;

// Mobile menu commands pass the intended songs and order to shared playback.
[Collection("PlatformDataDirectory")]
public class MobileQueueLineUpTests : PinnedDataDirectory
{
    // An album apiece, so album order and any other order differ - a menu
    // sorting a playlist by album is otherwise invisible.
    private static Track T(string title) => new() { Title = title, Album = title, Path = $"/music/{title}.mp3" };

    private static List<Track> Tracks(params string[] titles) => titles.Select(T).ToList();

    private static string[] Titles(PlaylistControlViewModel vm) => vm.CurrentPlaylist.Tracks.Select(t => t.Title!).ToArray();

    // ── The phone ─────────────────────────────────────────────────────────

    private static MainViewModelHarness.MobileParts Phone(List<Track> tracks)
    {
        var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(tracks));
        parts.AppSettings.MobileTabs = [nameof(MobileTab.Queue), nameof(MobileTab.Playlists)];
        var mobile = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying,
            NullLogger<MobileMainViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        return new MainViewModelHarness.MobileParts(mobile, parts);
    }

    private static string[] QueueTitles(MobileMainViewModel mobile) => mobile.QueueRows.Select(r => r.Track.Title!).ToArray();

    private static MainViewModelHarness.MobileParts PhonePlaying(params string[] titles)
    {
        var tracks = Tracks(titles);
        var scope = Phone(tracks);
        scope.Parts.Main.SetPlayQueue(tracks);
        scope.Parts.PlaylistControl.Play(tracks[0], 0);
        Dispatcher.UIThread.RunJobs();
        return scope;
    }

    [AvaloniaFact]
    public void A_songs_menu_plays_it_next()
    {
        using var scope = PhonePlaying("A", "B", "C");
        var mobile = scope.Mobile;

        mobile.OpenTrackActionsCommand.Execute(mobile.QueueRows[2]);
        mobile.PlayNextActionTargetCommand.Execute(null);

        Assert.Equal(new[] { "A", "C", "B", "C" }, QueueTitles(mobile));
        Assert.Equal(MobileSheet.None, mobile.ActiveSheet);
    }

    // Only on the queue's own rows, and not on the song playing.
    [AvaloniaFact]
    public void A_song_on_the_queue_can_be_taken_out_of_it_from_its_menu()
    {
        using var scope = PhonePlaying("A", "B", "C");
        var mobile = scope.Mobile;

        mobile.OpenTrackActionsCommand.Execute(mobile.QueueRows[0]);
        Assert.False(mobile.CanRemoveActionTargetFromQueue);
        mobile.CloseSheetCommand.Execute(null);

        mobile.OpenTrackActionsCommand.Execute(mobile.QueueRows[1]);
        Assert.True(mobile.CanRemoveActionTargetFromQueue);
        mobile.RemoveActionTargetFromQueueCommand.Execute(null);

        Assert.Equal(new[] { "A", "C" }, QueueTitles(mobile));
    }

    // In the playlist's own order - an album menu's songs otherwise go in
    // album order, which for a playlist would scramble it.
    [AvaloniaFact]
    public void A_playlists_menu_queues_it_in_its_own_order()
    {
        using var scope = PhonePlaying("A", "B", "C");
        var mobile = scope.Mobile;
        var library = scope.Parts.Library.Tracks;
        var playlist = new Playlist("Mix", [library.Single(t => t.Title == "C"), library.Single(t => t.Title == "B")]);
        var item = new SidebarItem(SidebarItemKind.Playlist, playlist.Name, playlist: playlist);

        mobile.OpenPlaylistActionsCommand.Execute(item);
        mobile.AddAlbumActionTargetToQueueCommand.Execute(null);

        Assert.Equal(new[] { "A", "B", "C", "C", "B" }, QueueTitles(mobile));
    }

    private static SidebarItem Mix(MainViewModelHarness.MobileParts scope, params string[] titles)
    {
        var library = scope.Parts.Library.Tracks;
        var playlist = new Playlist("Mix", titles.Select(t => library.Single(x => x.Title == t)).ToList());
        return new SidebarItem(SidebarItemKind.Playlist, playlist.Name, playlist: playlist);
    }

    [AvaloniaFact]
    public void A_playlists_menu_plays_it_in_its_own_order()
    {
        using var scope = PhonePlaying("A", "B", "C");
        var mobile = scope.Mobile;

        mobile.OpenPlaylistActionsCommand.Execute(Mix(scope, "C", "A", "B"));
        mobile.PlayAlbumActionTargetCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "C", "A", "B" }, Titles(scope.Parts.PlaylistControl));
        Assert.Equal("C", scope.Parts.PlaylistControl.CurrentlyPlayingTrack?.Title);
    }

    [AvaloniaFact]
    public void A_playlists_menu_adds_it_to_another_playlist_in_its_own_order()
    {
        using var scope = PhonePlaying("A", "B", "C");
        var mobile = scope.Mobile;
        var target = new Playlist("Other", []);
        scope.Parts.Library.AddPlaylist(target);

        mobile.OpenPlaylistActionsCommand.Execute(Mix(scope, "C", "A", "B"));
        mobile.AddAlbumActionTargetToPlaylistCommand.Execute(null);
        mobile.AddTrackToPlaylistCommand.Execute(new SidebarItem(SidebarItemKind.Playlist, target.Name, playlist: target));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "C", "A", "B" }, target.Tracks.Select(t => t.Title));
    }
}
