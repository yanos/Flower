using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Services;

namespace Flower.Importer;

// The browser head's playlist edits, pushed back to the origin server that
// lent it the playlists in the first place - the write half of
// OriginPlaylistImporter, and the end of "every browser path is read-only".
//
// Still not the peer-to-peer merge PlaylistSyncService runs, and deliberately
// so. A tab has no durable identity to be a party to a merge and nothing of
// its own to contribute: its playlists *are* the server's, so an edit made in
// a tab is an edit to the server's set and the honest way to send it is
// wholesale replacement - which is exactly what POST /playlists/apply already
// means (the peer path resolves every conflict on the initiator and posts the
// result; here the tab is the only editor, so there is nothing to resolve).
//
// Two things this has to get right that a single fire-and-forget POST would
// not:
//
//   Echo. Installing the fetched set raises Library.PlaylistsChanged exactly
//   as a user's own edit does, so without NoteOriginState every rescan would
//   push the server's own playlists back at it.
//
//   Ordering. A drag-reorder raises one change per move. Ten full-manifest
//   POSTs in flight at once can land in any order, and the last to *arrive*
//   wins - so an early, stale manifest landing last silently reverts the
//   edit. One push at a time, and the burst collapses to its final state.
public sealed class OriginPlaylistWriter(
    HttpClient http,
    string baseUrl,
    IPeerCredentials credentials,
    ILogger<OriginPlaylistWriter> logger) : IPlaylistWriter
{
    public const string ApplyPath = "/api/flower/v1/playlists/apply";

    // Inert: ApplyPlaylists reads the manifest's playlists and ignores whose
    // they claim to be (it identifies the caller from the request's own
    // credentials instead, which is the only version of that question with an
    // answer worth trusting). Named rather than left empty so a manifest
    // captured off the wire says where it came from.
    private const string BrowserFingerprint = "browser";

    private readonly object _gate = new();

    // The manifest the origin is believed to hold. Set from a successful push
    // and from NoteOriginState, compared against before every send.
    private string? _originState;

    private IReadOnlyList<Playlist>? _pending;

    public Task InFlight { get; private set; } = Task.CompletedTask;

    public void NoteOriginState(IReadOnlyList<Playlist> playlists)
    {
        lock (_gate)
        {
            _originState = Serialize(playlists);
        }
    }

    public void Schedule(IReadOnlyList<Playlist> playlists)
    {
        lock (_gate)
        {
            // Latest wins. The pump below re-reads this after every send, so a
            // change arriving mid-push is not lost - it just replaces whatever
            // else was waiting, since each push carries the whole set anyway.
            _pending = playlists;
            if (!InFlight.IsCompleted)
                return;

            InFlight = PumpAsync();
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            IReadOnlyList<Playlist> next;
            lock (_gate)
            {
                if (_pending == null)
                    return;

                next = _pending;
                _pending = null;
            }

            await PushAsync(next);
        }
    }

    private async Task PushAsync(IReadOnlyList<Playlist> playlists)
    {
        var json = Serialize(playlists);
        lock (_gate)
        {
            if (json == _originState)
                return;
        }

        try
        {
            var body = Encoding.UTF8.GetBytes(json);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}{ApplyPath}")
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            await request.AddPeerCredentialsAsync(credentials, body);

            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            lock (_gate)
            {
                _originState = json;
            }

            logger.LogInformation(
                "Pushed {PlaylistCount} playlist(s) to the origin server at {BaseUrl}", playlists.Count, baseUrl);
        }
        catch (Exception ex)
        {
            // _originState deliberately left alone, so the next edit re-sends
            // rather than assuming this one landed. Nothing is retried on its
            // own: a tab that cannot reach its origin has bigger problems
            // showing on screen already, and the edit is still in front of the
            // user to make again.
            logger.LogError(ex, "Could not push playlists to the origin server at {BaseUrl}", baseUrl);
        }
    }

    private static string Serialize(IReadOnlyList<Playlist> playlists) =>
        JsonSerializer.Serialize(
            PlaylistSyncMapper.ToManifest(BrowserFingerprint, playlists),
            PlaylistSyncJsonContext.Default.PlaylistSyncManifestDto);
}
