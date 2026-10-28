using System;
using System.Collections.Generic;
using System.Linq;

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

// One play, at one moment: a track heard (see ListenMeter), which moves the
// far side's play count and its LastPlayed together, as it does on the device
// that heard it (Library.IncrementPlayCount). A track started and skipped is
// not reported at all. There used to be two halves here, a start and a
// completion, sent minutes apart; History then showed songs the play count
// said were never played.
//
// EventId exists only so a retry is safe. A batch that reached the server and
// whose response was lost is re-sent by the reporter, and increments are not
// idempotent on their own - the server drops an id it has already applied.
public sealed record PlayEventDto(
    string EventId,
    string TrackId,
    DateTimeOffset At);

// POST /api/flower/v1/plays. A batch rather than one request per event
// because a failed send has to be able to carry its backlog along with the
// next one.
public sealed record PlayReportDto(List<PlayEventDto> Plays)
{
    // What one report may carry. The reporter holds at most this many (see
    // OriginPlayReporter.MaxQueued), so an honest batch never exceeds it, and
    // the server refuses a report that does - every event is a dedupe entry
    // it keeps for hours. Ids are a Guid's 32 hex characters from the
    // reporter and a catalog id the server minted; 64 bounds both.
    public const int MaxEvents = 500;
    public const int MaxIdLength = 64;

    // A method rather than a property, so the serializer does not put it on
    // the wire.
    public bool IsWithinLimits() =>
        Plays.Count <= MaxEvents
        && Plays.All(p => p.EventId is { Length: > 0 and <= MaxIdLength } && p.TrackId is { Length: <= MaxIdLength });
}

// POST /api/flower/v1/track-state - the same journey for a head that *does*
// hold durable storage: a paired desktop or phone playing, starring and
// configuring a track it got from its server.
//
// Everything here is one entry of Library.CarryForwardMutableState's list -
// THE list of what a rescan must not reset, which is the same thing as "what
// this device knows about the track that reading the file cannot tell you".
// A local device keeps all of it; a device listening to a shared library kept
// all of it too, and then kept it to itself. That is what this closes.
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
// The count is the only field with that property, and it is why the rest of
// this record is admin-only (see SyncEndpoints.ReportTrackState). A count is
// filed under the reporter's own name and adds to a total; LastPlayedAt,
// Starred and the playback options are single-valued fields on the shared
// library's copy of the track, so writing one is speaking for the library
// rather than for yourself. A housemate's phone may say "I played this twice".
// Only an owner's device may say "this library's copy of this track is
// starred".
//
// TrackId is the server's id for the track (Track.OriginTrackId), for the same
// reason PlayEventDto's is: the client's own Guid means nothing there.
//
// DateAdded is when the reporting device first had the song, and is the one
// field here the server only ever moves backwards - see
// Library.ApplyReportedOwnerState. Null from a device that is not an admin,
// like the rest of the owner state, and from one that has nothing older to say.
//
// Count is plays counted in Flower, on this device. ImportedCount is
// Music.app's count, read on this device (ITunesPlayCountImporter), and owner
// state like DateAdded: not this device's tally but the library's one number
// for the song, which the server raises to the highest any admin device
// reports. Kept apart from Count because the owner's Macs usually share one
// Music.app history, and summing each Mac's tally counted it once per Mac.
public sealed record TrackStateDto(
    string TrackId,
    int Count,
    DateTimeOffset? LastPlayedAt = null,
    bool Starred = false,
    bool RememberPlaybackPosition = false,
    double? ResumePositionSeconds = null,
    bool IgnoreWhenShuffling = false,
    int VolumeAdjustment = 0,
    DateTimeOffset? DateAdded = null,
    int ImportedCount = 0);

// The fingerprint the report belongs to is deliberately absent: the server
// files it under the one the request signature proved, because a body is
// attacker-controlled on a route every paired device can call, and believing
// it would let one device write another's tally.
public sealed record TrackStateReportDto(List<TrackStateDto> Tracks);

// The library token the server's catalog carried once a track-state report had
// been merged into it, returned on the response to that report.
//
// It exists to break a loop. A device reporting a play changes what the
// server's catalog says about that track, so the server's library token moves,
// so the /info poll that watches that token sees a change and pulls the whole
// catalog back - a catalog whose only difference is the thing the device just
// said. One play, one full-library fetch, on a five-second tick, for as long as
// music is playing. A phone's logs for a single day of listening carry 396 full
// fetches of a 16,116-track catalog against a normal day's 34.
//
// Handing the resulting token straight back lets the reporter recognise its own
// echo and not chase it. Every other reason a token moves - the owner editing
// something, a rescan, another device's report - still reads as a change and
// still syncs.
public static class TrackStateReportHeaders
{
    public const string LibraryToken = "X-Flower-Library-Token";
}
