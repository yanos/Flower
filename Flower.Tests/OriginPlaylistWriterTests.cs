using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Importer;
using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// The write half of the browser head's playlists - an edit made in a tab
// reaching the server the tab borrowed those playlists from.
//
// Two behaviours here are not about HTTP at all and are the reason this class
// exists rather than a single "it POSTs" test. The first is echo: installing
// the fetched set raises the same Library event a user's own edit does, so
// without NoteOriginState every rescan would hand the server its own playlists
// back. The second is ordering: each push carries the whole manifest, so two
// in flight at once means the *last to arrive* wins - and a burst (one change
// per move of a drag-reorder) can easily arrive out of order and silently
// revert what the user just did.
public class OriginPlaylistWriterTests
{
    private static readonly HttpClient Http = new();

    private static OriginPlaylistWriter Writer(FakePeerHttpServer server, string? credentialHeader = null) =>
        new(Http, $"http://127.0.0.1:{server.Port}",
            credentialHeader == null ? new NoCredentials() : new HeaderCredentials(credentialHeader),
            NullLogger<OriginPlaylistWriter>.Instance);

    private static Track TestTrack(string title) => new()
    {
        Path = $"/music/{title}.mp3",
        Title = title,
        Artists = "Remote Artist",
        Album = "Remote Album",
        Duration = TimeSpan.FromSeconds(180),
    };

    private static Playlist TestPlaylist(string name, params string[] titles) =>
        new(name, titles.Select(TestTrack).ToList());

    private sealed record Capture(string Path, string? Credential, PlaylistSyncManifestDto? Manifest);

    private static FakePeerHttpServer Server(
        List<Capture> captured,
        Func<Task>? gate = null,
        HttpStatusCode status = HttpStatusCode.NoContent) =>
        new(async context =>
        {
            using (var reader = new StreamReader(context.Request.InputStream))
            {
                captured.Add(new Capture(
                    context.Request.Url!.AbsolutePath,
                    context.Request.Headers["X-Test-Credential"],
                    JsonSerializer.Deserialize(
                        await reader.ReadToEndAsync(),
                        PlaylistSyncJsonContext.Default.PlaylistSyncManifestDto)));
            }

            if (gate != null)
                await gate();

            context.Response.StatusCode = (int)status;
            context.Response.ContentLength64 = 0;
            context.Response.Close();
        });

    [Fact]
    public async Task An_edit_made_in_a_tab_reaches_the_origin_as_the_whole_playlist_set()
    {
        var captured = new List<Capture>();
        using var server = Server(captured);
        var writer = Writer(server, credentialHeader: "session-token");

        writer.Schedule([TestPlaylist("Evening", "One", "Two")]);
        await writer.InFlight;

        var push = Assert.Single(captured);
        Assert.Equal(OriginPlaylistWriter.ApplyPath, push.Path);
        // The same gate GET /playlists sits behind - sending nothing here is a
        // 403 and an edit that silently never lands.
        Assert.Equal("session-token", push.Credential);

        var playlist = Assert.Single(push.Manifest!.Playlists);
        Assert.Equal("Evening", playlist.Name);
        Assert.Equal(["One", "Two"], playlist.Tracks.Select(t => t.Title));
    }

    [Fact]
    public async Task The_set_just_fetched_from_the_origin_is_not_pushed_straight_back_at_it()
    {
        var captured = new List<Capture>();
        using var server = Server(captured);
        var writer = Writer(server);
        var fetched = new List<Playlist> { TestPlaylist("Evening", "One", "Two") };

        // What the rescan does: note, then install - and installing raises
        // PlaylistsChanged, which is the subscription that calls Schedule.
        writer.NoteOriginState(fetched);
        writer.Schedule(fetched);
        await writer.InFlight;

        Assert.Empty(captured);
    }

    [Fact]
    public async Task A_real_edit_after_a_fetch_still_goes_out()
    {
        // The other side of the test above: suppression is per-content, not a
        // one-shot mute that would swallow the user's first edit of a session.
        var captured = new List<Capture>();
        using var server = Server(captured);
        var writer = Writer(server);

        writer.NoteOriginState([TestPlaylist("Evening", "One", "Two")]);
        writer.Schedule([TestPlaylist("Evening", "One")]);
        await writer.InFlight;

        Assert.Equal(["One"], Assert.Single(captured).Manifest!.Playlists.Single().Tracks.Select(t => t.Title));
    }

    [Fact]
    public async Task A_burst_of_edits_collapses_to_one_push_carrying_the_final_state()
    {
        // A drag-reorder raises one change per move. Held the first response
        // open so every later Schedule lands while a push is in flight, which
        // is exactly the window that would otherwise produce a pile of
        // overlapping full-manifest POSTs racing to be the last to arrive.
        var captured = new List<Capture>();
        var release = new TaskCompletionSource();
        var firstArrived = new TaskCompletionSource();
        using var server = Server(captured, gate: async () =>
        {
            firstArrived.TrySetResult();
            await release.Task;
        });
        var writer = Writer(server);

        writer.Schedule([TestPlaylist("Evening", "One")]);
        await firstArrived.Task;

        writer.Schedule([TestPlaylist("Evening", "One", "Two")]);
        writer.Schedule([TestPlaylist("Evening", "One", "Two", "Three")]);
        release.SetResult();
        await writer.InFlight;

        // Two pushes, not three: the middle state was overwritten while the
        // first was still on the wire, and never needed to exist on the far
        // side at all.
        Assert.Equal(2, captured.Count);
        Assert.Equal(["One"], captured[0].Manifest!.Playlists.Single().Tracks.Select(t => t.Title));
        Assert.Equal(["One", "Two", "Three"], captured[1].Manifest!.Playlists.Single().Tracks.Select(t => t.Title));
    }

    [Fact]
    public async Task A_push_the_origin_refused_is_sent_again_with_the_next_edit()
    {
        // A rejected push must not be recorded as the origin's state, or the
        // dedupe above would treat the server as holding an edit it never
        // accepted and the next identical set would be suppressed.
        var captured = new List<Capture>();
        using var server = Server(captured, status: HttpStatusCode.InternalServerError);
        var writer = Writer(server);

        writer.Schedule([TestPlaylist("Evening", "One")]);
        await writer.InFlight;
        writer.Schedule([TestPlaylist("Evening", "One")]);
        await writer.InFlight;

        Assert.Equal(2, captured.Count);
    }

    [Fact]
    public async Task An_origin_that_cannot_be_reached_leaves_the_tab_running()
    {
        // Fire-and-forget from an event handler: a throw here has nowhere to go
        // but TaskScheduler.UnobservedTaskException.
        var writer = new OriginPlaylistWriter(
            Http, $"http://127.0.0.1:{FakePeerHttpServer.GetUnboundPort()}",
            new NoCredentials(), NullLogger<OriginPlaylistWriter>.Instance);

        writer.Schedule([TestPlaylist("Evening", "One")]);
        await writer.InFlight;
    }

    private sealed class NoCredentials : IPeerCredentials
    {
        public Task<IReadOnlyList<(string Key, string Value)>> AuthorizeAsync(
            string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body) =>
            Task.FromResult<IReadOnlyList<(string Key, string Value)>>([]);
    }

    private sealed class HeaderCredentials(string value) : IPeerCredentials
    {
        public Task<IReadOnlyList<(string Key, string Value)>> AuthorizeAsync(
            string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body) =>
            Task.FromResult<IReadOnlyList<(string Key, string Value)>>([("X-Test-Credential", value)]);
    }
}
