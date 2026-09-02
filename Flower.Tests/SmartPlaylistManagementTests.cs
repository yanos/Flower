using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Material.Icons;

using Flower.Models;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// The other half of phase 4 (docs/SMART-PLAYLIST-PLAN.md, "UI"): every edit
// that a recompute would silently undo has to be refused, and refused here
// rather than only hidden in MainView - the sidebar drop, the Add To Playlist
// menu and the mobile view all arrive at these two methods.
public class SmartPlaylistManagementTests
{
    private static Track T(string title) => new Track { Title = title, Path = $"/music/{title}.mp3" };

    private sealed class NullPlaylistStore : IPlaylistStore
    {
        public void Save(IEnumerable<Playlist> playlists) { }
    }

    private sealed class RecordingHost : IPlaylistManagementHost
    {
        public SidebarItem? SelectedSidebarItem { get; set; }
        public SidebarItem? DefaultSelection => null;
        public int ContentChanges { get; private set; }
        public int SyncsScheduled { get; private set; }

        public void PlaylistContentChanged() => ContentChanges++;
        public void ScheduleContentSync() => SyncsScheduled++;
    }

    private static (PlaylistManagementViewModel Playlists, Library Library, RecordingHost Host) NewSubject(params Track[] tracks)
    {
        var library = new Library(tracks.ToList(), NullLogger<Library>.Instance, null, new NullPlaylistStore());
        var host = new RecordingHost();
        var items = new ObservableCollection<SidebarItem>();
        return (new PlaylistManagementViewModel(library, items, host), library, host);
    }

    private static Playlist Smart(Library library, string name)
    {
        var playlist = new Playlist(name, new List<Track>())
        {
            Rules = SmartPlaylistRules.MatchAll(
                new SmartCondition(SmartField.Title, SmartOperator.IsNotEmpty, SmartValue.None.Instance)),
        };
        library.AddPlaylist(playlist);
        return playlist;
    }

    [Fact]
    public async Task Tracks_cannot_be_added_to_a_smart_playlist()
    {
        var (playlists, library, host) = NewSubject(T("A"));
        var playlist = Smart(library, "Smart");

        await playlists.AddTracks([T("A")], playlist);

        Assert.Empty(playlist.Tracks);
        Assert.Equal(0, host.SyncsScheduled);
    }

    [Fact]
    public async Task Tracks_can_still_be_added_to_an_ordinary_playlist()
    {
        var (playlists, library, host) = NewSubject(T("A"));
        var playlist = new Playlist("Ordinary", new List<Track>());
        library.AddPlaylist(playlist);

        await playlists.AddTracks([T("A")], playlist);

        Assert.Single(playlist.Tracks);
        Assert.Equal(1, host.SyncsScheduled);
    }

    [Fact]
    public async Task A_smart_playlist_cannot_be_reordered()
    {
        var (playlists, library, _) = NewSubject();
        var first = T("A");
        var second = T("B");
        var playlist = Smart(library, "Smart");
        playlist.ReplaceAll([first, second]);

        await playlists.ReorderTrack(playlist, second, first);

        Assert.Equal(["A", "B"], playlist.Tracks.Select(t => t.Title));
    }

    // Freezing is a real user edit - unlike a recompute - so it bumps UpdatedAt
    // and gets propagated, and the songs it is showing stay exactly as they are.
    [Fact]
    public async Task Converting_to_ordinary_keeps_the_tracks_and_drops_the_rules()
    {
        var (playlists, library, host) = NewSubject();
        var playlist = Smart(library, "Smart");
        playlist.ReplaceAll([T("A"), T("B")]);
        var before = playlist.UpdatedAt;

        await playlists.ConvertToOrdinary(playlist);

        Assert.False(playlist.IsSmart);
        Assert.Equal(["A", "B"], playlist.Tracks.Select(t => t.Title));
        Assert.True(playlist.UpdatedAt > before);
        Assert.Equal(1, host.SyncsScheduled);
    }

    [Fact]
    public async Task A_converted_playlist_accepts_tracks_again()
    {
        var (playlists, library, _) = NewSubject(T("A"));
        var playlist = Smart(library, "Smart");

        await playlists.ConvertToOrdinary(playlist);
        await playlists.AddTracks([T("A")], playlist);

        Assert.Single(playlist.Tracks);
    }

    [Fact]
    public void A_new_smart_playlist_starts_ordinary_and_selected()
    {
        var (playlists, library, host) = NewSubject();

        var playlist = playlists.CreateSmart();

        Assert.Contains(playlist, library.Playlists);
        Assert.False(playlist.IsSmart);
        Assert.Same(playlist, host.SelectedSidebarItem?.Playlist);
    }

    // The only cue that a row will refuse a drop, and it has to be there before
    // the user tries rather than after.
    [Fact]
    public void The_sidebar_tells_the_two_kinds_of_playlist_apart()
    {
        var (playlists, library, _) = NewSubject();
        var smart = Smart(library, "Smart");
        var ordinary = new Playlist("Ordinary", new List<Track>());

        Assert.NotEqual(SidebarItem.IconFor(ordinary), SidebarItem.IconFor(smart));
        Assert.Equal(MaterialIconKind.PlaylistPlay, SidebarItem.IconFor(ordinary));
    }
}
