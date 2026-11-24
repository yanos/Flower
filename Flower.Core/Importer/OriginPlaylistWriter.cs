using System;
using System.Collections.Generic;
using System.Linq;
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
// Still not the merge PlaylistSyncService runs, and deliberately so. A tab has
// no durable identity to be a party to a merge and nothing of its own to
// contribute: its playlists *are* the server's, so an edit made in a tab is
// written over the revision the tab read it at, and a deletion is named. A
// change the server answers Stale (another device wrote it first) is not
// written, and is left for the next refresh to replace with the server's -
// the server never takes a write over a version it has moved on from.
//
// Two things this has to get right that a single fire-and-forget POST would
// not:
//
//   Echo. Installing the fetched set raises Library.PlaylistsChanged exactly
//   as a user's own edit does, so without NoteOriginState every rescan would
//   push the server's own playlists back at it.
//
//   Ordering. A drag-reorder raises one change per move. One push at a time,
//   and the burst collapses to its final state.
public sealed class OriginPlaylistWriter(
    HttpClient http,
    string baseUrl,
    IPeerCredentials credentials,
    ILogger<OriginPlaylistWriter> logger) : IPlaylistWriter
{
    public const string ChangesPath = "/api/flower/v1/playlists/changes";

    private readonly object _gate = new();

    // What the origin is believed to hold, by playlist: its revision and which
    // content it is (PlaylistVersion). Set from NoteOriginState and from each
    // change the origin applied.
    private Dictionary<Guid, (long Revision, string Version)> _origin = [];

    private IReadOnlyList<Playlist>? _pending;

    public Task InFlight { get; private set; } = Task.CompletedTask;

    public void NoteOriginState(OriginPlaylists origin)
    {
        lock (_gate)
        {
            _origin = origin.Playlists.ToDictionary(
                p => p.Id,
                p => (origin.Revisions.GetValueOrDefault(p.Id), PlaylistVersion.Of(p)));
        }
    }

    public void Schedule(IReadOnlyList<Playlist> playlists)
    {
        lock (_gate)
        {
            // Latest wins. The pump below re-reads this after every send, so a
            // change arriving mid-push is not lost - it just replaces whatever
            // else was waiting, since each is worked out from the whole set.
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
        var changes = new List<PlaylistChangeDto>();
        var versions = new Dictionary<Guid, string>();
        lock (_gate)
        {
            foreach (var playlist in playlists)
            {
                var dto = PlaylistSyncMapper.ToDto(playlist);
                var version = PlaylistVersion.Of(dto);
                var known = _origin.TryGetValue(playlist.Id, out var held);
                if (known && held.Version == version)
                    continue;

                versions[playlist.Id] = version;
                changes.Add(new PlaylistChangeDto(playlist.Id, known ? held.Revision : 0, dto));
            }

            var ids = playlists.Select(p => p.Id).ToHashSet();
            changes.AddRange(_origin.Where(e => !ids.Contains(e.Key))
                .Select(e => new PlaylistChangeDto(e.Key, e.Value.Revision, Delete: true)));
        }

        if (changes.Count == 0)
            return;

        try
        {
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new PlaylistChangesRequestDto(changes), PlaylistSyncJsonContext.Default.PlaylistChangesRequestDto));

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}{ChangesPath}")
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            await request.AddPeerCredentialsAsync(credentials, body);

            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var answer = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(), PlaylistSyncJsonContext.Default.PlaylistChangesResponseDto);

            lock (_gate)
            {
                foreach (var applied in answer?.Answers ?? [])
                {
                    if (applied.Outcome != PlaylistChangeOutcome.Applied)
                    {
                        // Left as the origin last said: the next push is based
                        // on that same old revision and is refused again, so a
                        // stale copy in this tab can never be written over a
                        // newer one. The next refresh shows the server's.
                        logger.LogWarning(
                            "The origin server at {BaseUrl} did not take this tab's change to playlist {PlaylistId}: {Outcome}",
                            baseUrl, applied.Id, applied.Outcome);
                        continue;
                    }

                    if (versions.TryGetValue(applied.Id, out var version))
                        _origin[applied.Id] = (applied.Revision, version);
                    else
                        _origin.Remove(applied.Id);
                }
            }

            logger.LogInformation(
                "Pushed {ChangeCount} playlist change(s) to the origin server at {BaseUrl}", changes.Count, baseUrl);
        }
        catch (Exception ex)
        {
            // _origin deliberately left alone, so the next edit re-sends
            // rather than assuming this one landed. Nothing is retried on its
            // own: a tab that cannot reach its origin has bigger problems
            // showing on screen already, and the edit is still in front of the
            // user to make again.
            logger.LogError(ex, "Could not push playlists to the origin server at {BaseUrl}", baseUrl);
        }
    }
}
