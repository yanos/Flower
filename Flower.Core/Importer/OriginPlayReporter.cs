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

// The browser head's plays, reported to the origin server that lent it the
// track - the listening half of what OriginPlaylistWriter does for editing,
// and the last thing a tab changed that stayed in the tab.
//
// Three things this has to get right that a fire-and-forget POST per play
// would not:
//
//   Addressing. A tab's Track is a placeholder built from the server's own
//   catalog entry, so the id the server knows it by is OriginTrackId, not the
//   local Guid - which is minted fresh in the tab and means nothing there. A
//   track with no OriginTrackId came from somewhere else and is not this
//   server's to count; it is dropped rather than guessed at.
//
//   Ordering and backlog. Plays arrive during playback, one per track change
//   at least, and a send that fails must not lose them - so events queue, one
//   POST is in flight at a time, and a failed batch goes back to the front of
//   the queue to leave with the next one. This is the one real difference from
//   the playlist writer, which deliberately does not retry: a rejected
//   playlist edit is still on screen for the user to make again, and a
//   swallowed play is simply gone.
//
//   Duplicates, which that retry creates. A batch the server applied but whose
//   response never came back is re-sent, and an increment applied twice is
//   wrong in a way a wholesale replacement never is. Each event carries an id
//   the server drops on sight if it has already applied it (see
//   PlayReportService).
public sealed class OriginPlayReporter(
    HttpClient http,
    string baseUrl,
    IPeerCredentials credentials,
    ILogger<OriginPlayReporter> logger) : IPlayReporter
{
    public const string PlaysPath = "/api/flower/v1/plays";

    // Bounds the backlog a tab that cannot reach its origin builds up. Well
    // past a long offline listening session at one or two events per track,
    // and small enough that it cannot grow into the reason the tab is slow.
    // Oldest first, because the newest plays are the ones still worth having.
    private const int MaxQueued = 500;

    private readonly object _gate = new();
    private readonly List<PlayEventDto> _queued = new();

    // Whether the pump is still running, decided under _gate rather than read
    // off InFlight.IsCompleted. A Task is marked complete a moment *after* the
    // method returns, so a play arriving in that window would see a pump that
    // looked alive, decline to start one, and sit in the queue until the next
    // play happened to start one - which for the last track of a session is
    // never.
    private bool _pumping;

    public Task InFlight { get; private set; } = Task.CompletedTask;

    public void Report(Track track, TrackStatsChange change)
    {
        if (track.OriginTrackId is not { Length: > 0 } originTrackId)
        {
            logger.LogDebug(
                "Not reporting a play of {Title}: it carries no origin track id, so this server has nothing to count it against",
                track.Title);
            return;
        }

        var play = new PlayEventDto(
            Guid.NewGuid().ToString("N"),
            originTrackId,
            DateTimeOffset.UtcNow,
            change.HasFlag(TrackStatsChange.Started),
            change.HasFlag(TrackStatsChange.Finished));

        lock (_gate)
        {
            _queued.Add(play);
            if (_queued.Count > MaxQueued)
            {
                logger.LogWarning(
                    "Dropping the oldest of {Count} unsent play reports - the origin server at {BaseUrl} has been unreachable",
                    _queued.Count, baseUrl);
                _queued.RemoveRange(0, _queued.Count - MaxQueued);
            }

            if (_pumping)
                return;

            _pumping = true;
            InFlight = PumpAsync();
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            List<PlayEventDto> batch;
            lock (_gate)
            {
                if (_queued.Count == 0)
                {
                    _pumping = false;
                    return;
                }

                batch = new List<PlayEventDto>(_queued);
                _queued.Clear();
            }

            if (!await PushAsync(batch))
            {
                // Back to the front, ahead of anything that arrived while this
                // was in flight, so the queue stays in the order the plays
                // happened. Then stop: a second immediate attempt would fail
                // the same way. The next play to be reported starts the pump
                // again and carries the backlog with it.
                lock (_gate)
                {
                    _queued.InsertRange(0, batch);
                    _pumping = false;
                }

                return;
            }
        }
    }

    private async Task<bool> PushAsync(List<PlayEventDto> batch)
    {
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(
                new PlayReportDto(batch), PlayReportJsonContext.Default.PlayReportDto);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}{PlaysPath}")
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.AddPeerCredentials(credentials, body);

            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            logger.LogDebug(
                "Reported {PlayCount} play event(s) to the origin server at {BaseUrl}", batch.Count, baseUrl);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not report {PlayCount} play event(s) to the origin server at {BaseUrl}", batch.Count, baseUrl);
            return false;
        }
    }
}
