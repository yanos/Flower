using System;
using System.Collections.Generic;

namespace Flower.Services;

// Wire shapes for a head reporting what it played back to the server that
// lent it the track - see IPlayReporter and SYNC-PLAN.md's browser section.
//
// A play is reported as an event rather than as a count, which is the one
// place this protocol has to differ from the playlist half next door. A tab's
// playlists can be pushed as a set because the tab holds the whole set; a
// tab's *listening* cannot, because a tab holds no durable counter to state a
// total from - its whole filesystem is an in-memory WASM one that a refresh
// resets. So the tab reports the increments as they happen and the server,
// which is the only side with durable storage, keeps the running total.
//
// That is also why RemotePlayCounts - the per-device G-Counter the desktop
// peers merge by max, see Track.RemotePlayCounts - is the wrong instrument
// here despite being the obvious one: a counter that resets to zero on every
// refresh converges, under max-merge, on "whatever the highest single session
// happened to reach", silently discarding everything after it.

// One play, at one moment. Started and Completed are the two halves Flower
// keeps deliberately separate (see Library.TrackStatsChange): a skipped track
// reports a start and never a completion, so the far side's History matches
// what the user actually put on without its play count claiming they listened.
//
// EventId exists only so a retry is safe. A batch that reached the server and
// whose response was lost is re-sent by the reporter, and increments are not
// idempotent on their own - the server drops an id it has already applied.
public sealed record PlayEventDto(
    string EventId,
    string TrackId,
    DateTimeOffset At,
    bool Started,
    bool Completed);

// POST /api/flower/v1/plays. A batch rather than one request per event
// because the two halves of a single track's play are usually minutes apart
// but the tail of one track and the head of the next are not, and because a
// failed send has to be able to carry its backlog along with the next one.
public sealed record PlayReportDto(List<PlayEventDto> Plays);

// POST /api/flower/v1/play-counts - the same journey for a head that *does*
// hold a durable counter: a paired desktop or phone playing a track it got
// from its server.
//
// Totals, not events, which is the whole difference from the tab above. A
// device with durable storage can state "I have played this eleven times", and
// under Track.RemotePlayCounts' per-key max-merge that statement is idempotent:
// re-sending it after a failed push, or after a restart, converges instead of
// double-counting, so there is no event id to allocate and nothing for the
// server to remember having applied.
//
// It also keeps the play attributed to the device that made it. A tab's plays
// have to land in the server's own count because a tab is nobody; a paired
// device is somebody, and its count travels under its own fingerprint - the
// same shape it would have arrived in back when both ends served catalogs to
// each other.
//
// TrackId is the server's id for the track (Track.OriginTrackId), for the same
// reason PlayEventDto's is: the client's own Guid means nothing there.
public sealed record TrackPlayCountDto(string TrackId, int Count);

// The fingerprint the counts belong to is deliberately absent: the server
// files them under the one the request signature proved, because a body is
// attacker-controlled on a route every paired device can call, and believing
// it would let one device write another's tally.
public sealed record PlayCountReportDto(List<TrackPlayCountDto> Counts);
