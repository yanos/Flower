using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// New Vibe Playlist… and Edit Vibe Playlist… (VibePlaylistEditorViewModel).
public class VibePlaylistEditorTests
{
    private sealed class Server : IVibePlaylists
    {
        public List<(string Prompt, bool Instrumental)> Asked { get; } = [];
        public VibePreview Answer { get; set; } = new([]);

        public Task<VibePreview> PreviewAsync(string prompt, bool instrumental, CancellationToken ct = default)
        {
            Asked.Add((prompt, instrumental));
            return Task.FromResult(Answer);
        }

        public Task<IReadOnlyList<Track>> ExtendAsync(Playlist playlist, IEnumerable<Track> extensions, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Track>>([]);
    }

    static VibePlaylistEditorTests() => VibePlaylistEditorViewModel.TypingPause = TimeSpan.Zero;

    private readonly List<Track> _songs = Enumerable.Range(0, 5).Select(n => new Track { Title = $"Song {n}", Path = $"/m/{n}.mp3" }).ToList();
    private readonly Server _server = new();
    private readonly Library _library;

    public VibePlaylistEditorTests() => _library = new Library(_songs);

    private (VibePlaylistEditorViewModel Editor, Playlist Playlist) New()
    {
        var playlist = new Playlist("New Vibe Playlist", []);
        _library.AddPlaylist(playlist);
        return (new VibePlaylistEditorViewModel(playlist, _library, _server, isNew: true), playlist);
    }

    [Fact]
    public async Task A_sentence_asking_for_no_singing_turns_No_vocals_on_and_the_preview_asks_without_them()
    {
        var (editor, _) = New();

        editor.Prompt = "sad piano without lyrics";
        await editor.PreviewPass;

        Assert.True(editor.Instrumental);
        Assert.Equal(("sad piano without lyrics", true), _server.Asked.Last());
    }

    [Fact]
    public async Task The_preview_shows_the_servers_songs_and_says_how_many()
    {
        _server.Answer = new VibePreview(_songs.Take(3).ToList());
        var (editor, _) = New();

        editor.Prompt = "music for a rainy day";
        await editor.PreviewPass;

        Assert.Equal(_songs.Take(3), editor.PreviewTracks);
        Assert.Contains("3 best matches", editor.PreviewSummary);
    }

    [Theory]
    [InlineData(VibeRefusal.Off, "off on the server")]
    [InlineData(VibeRefusal.NotReady, "hasn't listened")]
    [InlineData(VibeRefusal.Unreachable, "Couldn't reach")]
    public async Task A_preview_the_server_refuses_says_why(VibeRefusal refusal, string says)
    {
        _server.Answer = new VibePreview([], refusal);
        var (editor, _) = New();

        editor.Prompt = "music for a rainy day";
        await editor.PreviewPass;

        Assert.Contains(says, editor.PreviewSummary);
        Assert.Empty(editor.PreviewTracks);
    }

    [Fact]
    public async Task Saving_without_a_name_names_the_playlist_after_its_sentence_and_keeps_the_preview_as_its_songs()
    {
        _server.Answer = new VibePreview(_songs.Take(2).ToList());
        var (editor, playlist) = New();
        editor.Prompt = "music for a rainy day";
        await editor.PreviewPass;

        Assert.True(editor.Save());

        Assert.Equal("Music for a rainy day", playlist.Name);
        Assert.Equal("music for a rainy day", playlist.VibePrompt);
        Assert.True(playlist.IsVibe);
        Assert.False(playlist.IsOrdinary);
        Assert.Equal(_songs.Take(2), playlist.Tracks);
    }

    [Fact]
    public void Nothing_to_save_without_a_sentence()
    {
        var (editor, playlist) = New();
        editor.Prompt = "   ";

        Assert.False(editor.CanSave);
        Assert.False(editor.Save());
        Assert.False(playlist.IsVibe);
    }

    [Fact]
    public void Cancelling_a_new_one_removes_it_and_cancelling_an_edit_does_not()
    {
        var (editor, playlist) = New();
        editor.Cancel();
        Assert.DoesNotContain(playlist, _library.Playlists);

        var existing = new Playlist("Rainy", []) { VibePrompt = "music for a rainy day" };
        _library.AddPlaylist(existing);
        new VibePlaylistEditorViewModel(existing, _library, _server).Cancel();
        Assert.Contains(existing, _library.Playlists);
    }

    [Fact]
    public async Task Editing_opens_on_the_playlists_sentence_and_shows_its_songs_at_once()
    {
        _server.Answer = new VibePreview(_songs.Take(1).ToList());
        var playlist = new Playlist("Rainy", []) { VibePrompt = "music for a rainy day", VibeInstrumental = true };
        _library.AddPlaylist(playlist);

        var editor = new VibePlaylistEditorViewModel(playlist, _library, _server);
        await editor.PreviewPass;

        Assert.Equal("Rainy", editor.Name);
        Assert.Equal("music for a rainy day", editor.Prompt);
        Assert.True(editor.Instrumental);
        Assert.Equal(("music for a rainy day", true), _server.Asked.Single());
    }
}
