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

namespace Flower.Tests.Browser;

// The write half of the browser head's playlists - an edit made in a tab
// reaching the server the tab borrowed those playlists from, written over the
// revision the tab read it at.
//
// Two behaviours here are not about HTTP at all and are the reason this class
// exists rather than a single "it POSTs" test. The first is echo: installing
// the fetched set raises the same Library event a user's own edit does, so
// without NoteOriginState every rescan would hand the server its own playlists
// back. The second is ordering: a burst (one change per move of a
// drag-reorder) collapses to its final state, one push at a time.
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

    private static OriginPlaylists Fetched(long revision, params Playlist[] playlists) =>
        new(playlists.ToList(), playlists.ToDictionary(p => p.Id, _ => revision));

    private sealed record Capture(string Path, string? Credential, PlaylistChangesRequestDto? Request);

    // Answers each change Applied at the next revision, the way the server
    // does - unless status says otherwise.
    private static FakePeerHttpServer Server(
        List<Capture> captured,
        Func<Task>? gate = null,
        HttpStatusCode status = HttpStatusCode.OK,
        PlaylistChangeOutcome outcome = PlaylistChangeOutcome.Applied)
    {
        var revision = 100L;
        return new(async context =>
        {
            PlaylistChangesRequestDto? request;
            using (var reader = new StreamReader(context.Request.InputStream))
            {
                request = JsonSerializer.Deserialize(
                    await reader.ReadToEndAsync(), PlaylistSyncJsonContext.Default.PlaylistChangesRequestDto);
                captured.Add(new Capture(context.Request.Url!.AbsolutePath, context.Request.Headers["X-Test-Credential"], request));
            }

            if (gate != null)
                await gate();

            context.Response.StatusCode = (int)status;
            var body = status == HttpStatusCode.OK
                ? JsonSerializer.SerializeToUtf8Bytes(
                    new PlaylistChangesResponseDto(request!.Changes
                        .Select(c => new PlaylistChangeAnswerDto(c.Id, outcome, ++revision)).ToList()),
                    PlaylistSyncJsonContext.Default.PlaylistChangesResponseDto)
                : [];
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });
    }

    private static List<string?> Titles(PlaylistChangeDto change) => change.Playlist!.Tracks.Select(t => t.Title).ToList();

    [Fact]
    public async Task A_playlist_made_in_a_tab_reaches_the_origin_as_new()
    {
        var captured = new List<Capture>();
        using var server = Server(captured);
        var writer = Writer(server, credentialHeader: "session-token");

        writer.Schedule([TestPlaylist("Evening", "One", "Two")]);
        await writer.InFlight;

        var push = Assert.Single(captured);
        Assert.Equal(PlaylistChangesClient.Path, push.Path);
        // The same gate GET /playlists sits behind - sending nothing here is a
        // 403 and an edit that silently never lands.
        Assert.Equal("session-token", push.Credential);

        var change = Assert.Single(push.Request!.Changes);
        Assert.Equal(0, change.BaseRevision);
        Assert.Equal("Evening", change.Playlist!.Name);
        Assert.Equal(["One", "Two"], Titles(change));
    }

    [Fact]
    public async Task The_set_just_fetched_from_the_origin_is_not_pushed_straight_back_at_it()
    {
        var captured = new List<Capture>();
        using var server = Server(captured);
        var writer = Writer(server);
        var fetched = TestPlaylist("Evening", "One", "Two");

        // What the rescan does: note, then install - and installing raises
        // PlaylistsChanged, which is the subscription that calls Schedule.
        writer.NoteOriginState(Fetched(7, fetched));
        writer.Schedule([fetched]);
        await writer.InFlight;

        Assert.Empty(captured);
    }

    // The other side of the test above: suppression is per-content, not a
    // one-shot mute that would swallow the user's first edit of a session -
    // and the edit is written over the revision the tab read.
    [Fact]
    public async Task An_edit_after_a_fetch_goes_out_over_the_revision_it_was_read_at()
    {
        var captured = new List<Capture>();
        using var server = Server(captured);
        var writer = Writer(server);
        var evening = TestPlaylist("Evening", "One", "Two");
        var morning = TestPlaylist("Morning", "Three");
        writer.NoteOriginState(Fetched(7, evening, morning));

        evening.RemoveEntriesAt([1]);
        writer.Schedule([evening, morning]);
        await writer.InFlight;

        var change = Assert.Single(Assert.Single(captured).Request!.Changes);
        Assert.Equal((evening.Id, 7L), (change.Id, change.BaseRevision));
        Assert.Equal(["One"], Titles(change));
    }

    [Fact]
    public async Task A_playlist_deleted_in_a_tab_is_named_as_deleted()
    {
        var captured = new List<Capture>();
        using var server = Server(captured);
        var writer = Writer(server);
        var (kept, doomed) = (TestPlaylist("Kept", "One"), TestPlaylist("Doomed", "Two"));
        writer.NoteOriginState(Fetched(7, kept, doomed));

        writer.Schedule([kept]);
        await writer.InFlight;

        var change = Assert.Single(Assert.Single(captured).Request!.Changes);
        Assert.Equal((doomed.Id, 7L, true), (change.Id, change.BaseRevision, change.Delete));
    }

    [Fact]
    public async Task A_burst_of_edits_collapses_to_one_push_carrying_the_final_state()
    {
        // A drag-reorder raises one change per move. Held the first response
        // open so every later Schedule lands while a push is in flight.
        var captured = new List<Capture>();
        var release = new TaskCompletionSource();
        var firstArrived = new TaskCompletionSource();
        using var server = Server(captured, gate: async () =>
        {
            firstArrived.TrySetResult();
            await release.Task;
        });
        var writer = Writer(server);
        var evening = TestPlaylist("Evening", "One");

        writer.Schedule([evening]);
        await firstArrived.Task;

        evening.AppendTrack(TestTrack("Two"));
        writer.Schedule([evening]);
        evening.AppendTrack(TestTrack("Three"));
        writer.Schedule([evening]);
        release.SetResult();
        await writer.InFlight;

        // Two pushes, not three, and the second over the revision the first
        // was given.
        Assert.Equal(2, captured.Count);
        Assert.Equal(["One"], Titles(Assert.Single(captured[0].Request!.Changes)));
        var second = Assert.Single(captured[1].Request!.Changes);
        Assert.Equal(["One", "Two", "Three"], Titles(second));
        Assert.Equal(101, second.BaseRevision);
    }

    // A rejected push must not be recorded as the origin's state, or the
    // dedupe above would treat the server as holding an edit it never
    // accepted.
    [Fact]
    public async Task A_push_the_origin_failed_is_sent_again_with_the_next_edit()
    {
        var captured = new List<Capture>();
        using var server = Server(captured, status: HttpStatusCode.InternalServerError);
        var writer = Writer(server);
        var evening = TestPlaylist("Evening", "One");

        writer.Schedule([evening]);
        await writer.InFlight;
        writer.Schedule([evening]);
        await writer.InFlight;

        Assert.Equal(2, captured.Count);
    }

    // Another device wrote it first. The tab's copy is never written over the
    // newer one: it goes again over the same old revision, and is refused
    // again, until the next refresh shows the server's.
    [Fact]
    public async Task A_stale_change_is_never_sent_over_a_newer_revision()
    {
        var captured = new List<Capture>();
        using var server = Server(captured, outcome: PlaylistChangeOutcome.Stale);
        var writer = Writer(server);
        var evening = TestPlaylist("Evening", "One");
        writer.NoteOriginState(Fetched(7, evening));

        evening.AppendTrack(TestTrack("Two"));
        writer.Schedule([evening]);
        await writer.InFlight;
        writer.Schedule([evening]);
        await writer.InFlight;

        Assert.All(captured, c => Assert.Equal(7, Assert.Single(c.Request!.Changes).BaseRevision));
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
