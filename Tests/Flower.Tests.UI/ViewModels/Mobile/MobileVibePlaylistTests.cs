using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests.ViewModels.Mobile;

// Vibe Playlists on a paired phone (docs/VIBE-PLAYLIST-PLAN.md, phase 4): the
// Playlists tab's New Vibe Playlist row and the editor as a pushed screen, the
// same MainViewModel calls and editor view model the desktop uses - and a
// Vibe Playlist playing as its endless queue from every door an ordinary
// playlist plays from.
[Collection("PlatformDataDirectory")]
public class MobileVibePlaylistTests : PinnedDataDirectory
{
    public MobileVibePlaylistTests() => TestIoc.EnsureConfigured();

    static MobileVibePlaylistTests() => VibePlaylistEditorViewModel.TypingPause = TimeSpan.Zero;

    private const string Rainy = "Rainy";

    private sealed class Server(IReadOnlyList<Track> answer) : IVibePlaylists
    {
        public Task<VibePreview> PreviewAsync(string prompt, bool instrumental, CancellationToken ct = default) =>
            Task.FromResult(new VibePreview(answer));

        public Task<IReadOnlyList<Track>> ExtendAsync(Playlist playlist, IEnumerable<Track> extensions, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Track>>([]);

        public Task<IReadOnlyList<Track>> EndlessAsync(IReadOnlyList<Track> seeds, IEnumerable<Track> exclude, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Track>>([]);
    }

    private readonly List<Track> _songs = Enumerable.Range(0, 20)
        .Select(n => new Track { Title = $"Song {n}", Album = $"Album {n}", Artists = $"Artist {n}", Path = $"/m/{n}.mp3", DateAdded = DateTimeOffset.UtcNow })
        .ToList();

    private MainViewModelHarness.MobileParts Build(string? serverState = "ready")
    {
        var library = new Library(_songs);
        library.AddPlaylist(new Playlist(Rainy, _songs) { VibePrompt = "music for a rainy day" });
        var parts = Own(MainViewModelHarness.BuildMobile(library, new MainPlaylist(new List<Track>())));
        parts.Parts.Main.VibePlaylists = new Server(_songs.Take(5).ToList());
        parts.Parts.Main.VibePlaylistsState = () => serverState;
        Dispatcher.UIThread.RunJobs();
        return parts;
    }

    private static MobileMainViewModel DrilledIn(MainViewModelHarness.MobileParts parts)
    {
        var vm = parts.Mobile;
        vm.SelectTabCommand.Execute(nameof(MobileTab.Playlists));
        UiWait.Settle(() => vm.PlaylistPickerItems.Any(i => i.Name == Rainy), "the playlist never reached the picker");
        vm.SelectPlaylistCommand.Execute(vm.PlaylistPickerItems.First(i => i.Name == Rainy));
        UiWait.Settle(() => vm.IsShowingPlaylistTracks && vm.Main.Rows.Count == 20, "never drilled into the playlist");
        return vm;
    }

    [AvaloniaFact]
    public void The_row_is_offered_only_while_the_server_makes_them()
    {
        Assert.True(Build("ready").Mobile.CanCreateVibePlaylist);
        Assert.True(Build("analysing").Mobile.CanCreateVibePlaylist);
        Assert.False(Build(null).Mobile.CanCreateVibePlaylist);
    }

    [AvaloniaFact]
    public void Backing_out_of_a_new_one_removes_it()
    {
        var parts = Build();
        var vm = parts.Mobile;

        vm.NewVibePlaylistCommand.Execute(null);
        Assert.True(vm.IsShowingVibePlaylistEditor);
        var draft = vm.VibePlaylistEditor!.Playlist;
        Assert.DoesNotContain(draft, parts.Parts.Library.Playlists);

        vm.CloseSheetCommand.Execute(null);

        Assert.False(vm.IsShowingVibePlaylistEditor);
        Assert.DoesNotContain(draft, parts.Parts.Library.Playlists);
    }

    [AvaloniaFact]
    public async Task The_check_saves_the_sentence_with_the_songs_it_previewed()
    {
        var parts = Build();
        var vm = parts.Mobile;
        vm.NewVibePlaylistCommand.Execute(null);
        var editor = vm.VibePlaylistEditor!;

        editor.Prompt = "music to go jogging";
        await editor.PreviewPass;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(5, vm.VibePlaylistPreviewRows.Count);
        vm.SaveVibePlaylistCommand.Execute(null);

        Assert.False(vm.IsShowingVibePlaylistEditor);
        var saved = Assert.Single(parts.Parts.Library.Playlists, p => p.Name == "Music to go jogging");
        Assert.Equal("music to go jogging", saved.VibePrompt);
        Assert.Equal(_songs.Take(5), saved.Tracks);
    }

    [AvaloniaFact]
    public void A_song_tapped_on_its_screen_starts_the_endless_queue_from_it()
    {
        var parts = Build();
        var vm = DrilledIn(parts);
        var row = vm.Main.Rows[7];

        vm.PlayTrackCommand.Execute(row);

        var playback = parts.Parts.PlaylistControl;
        Assert.Same(row.Track, playback.CurrentlyPlayingTrack);
        Assert.Equal(Rainy, playback.VibeSource?.Playlist.Name);
    }

    [AvaloniaFact]
    public void Its_header_play_starts_the_endless_queue()
    {
        var parts = Build();
        var vm = DrilledIn(parts);

        vm.PlayAlbumCommand.Execute(null);

        Assert.Equal(Rainy, parts.Parts.PlaylistControl.VibeSource?.Playlist.Name);
    }

    [AvaloniaFact]
    public void Its_row_menu_edits_the_sentence_has_no_shuffle_and_plays_the_endless_queue()
    {
        var parts = Build();
        var vm = parts.Mobile;
        vm.SelectTabCommand.Execute(nameof(MobileTab.Playlists));
        UiWait.Settle(() => vm.PlaylistPickerItems.Any(i => i.Name == Rainy), "the playlist never reached the picker");
        var row = vm.PlaylistPickerItems.First(i => i.Name == Rainy);

        vm.OpenPlaylistActionsCommand.Execute(row);
        var actions = vm.AlbumActions.Select(e => e.Action).ToList();
        Assert.Contains(MenuAction.EditVibe, actions);
        Assert.DoesNotContain(MenuAction.Shuffle, actions);

        vm.RunAlbumActionCommand.Execute(vm.AlbumActions.Single(e => e.Action == MenuAction.Play));
        Assert.Equal(Rainy, parts.Parts.PlaylistControl.VibeSource?.Playlist.Name);

        vm.OpenPlaylistActionsCommand.Execute(row);
        vm.RunAlbumActionCommand.Execute(vm.AlbumActions.Single(e => e.Action == MenuAction.EditVibe));
        Assert.True(vm.IsShowingVibePlaylistEditor);
        Assert.Same(row.Playlist, vm.VibePlaylistEditor!.Playlist);
    }

    [AvaloniaFact]
    public void Its_screen_menu_edits_the_sentence_and_has_no_shuffle()
    {
        var parts = Build();
        var vm = DrilledIn(parts);

        var entries = vm.BuildScreenMenu(vm.CurrentFrame).SelectMany(s => s.Entries).ToList();
        Assert.DoesNotContain(entries, e => e.Label == "Shuffle");
        entries.Single(e => e.Label == "Edit").Invoke();

        Assert.True(vm.IsShowingVibePlaylistEditor);
        Assert.Equal(Rainy, vm.VibePlaylistEditor!.Playlist.Name);
    }
}
