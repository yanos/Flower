using System;

namespace Flower.Models;

// The one place a Guid entity id turns into a string and back.
//
// Two vocabularies, and exactly two, both spelled here so a write and its
// matching read can never drift apart:
//
//   Key   - how an id is stored: 32 lowercase hex characters, no dashes.
//           Every id column in Flower.Core's SQLite schema holds this shape
//           (tracks.id, playlists.id, playlist_tracks.*, track_remote_play_counts.track_id),
//           and it is also what both hosts hand out over OpenSubsonic.
//   Wire  - how an id arrives from outside: whatever a client sent. Parsed
//           leniently, because a third-party Subsonic client is free to round-
//           trip an id through its own Guid type and hand it back dashed.
//
// The asymmetry is deliberate and is the whole reason this type exists. It
// used to be implicit - eight inline ToString("N")/Guid.Parse pairs - and the
// gap between the two bit us: a dashed id resolved in memory through the
// lenient parse and was then forwarded verbatim into a WHERE clause against a
// hex column, so a star succeeded in the API and matched no row. Anything
// crossing into storage goes through ToKey, never through a caller's spelling.
//
// Album and artist ids are NOT Guids - they are content hashes with a single
// spelling (see SubsonicIdentity) and need no conversion at all.
public static class EntityId
{
    // Guid -> stored/published id. The only formatting of an id in the codebase.
    public static string ToKey(this Guid id) => id.ToString("N");

    // Stored id -> Guid. Exact, not lenient: this reads rows this code wrote,
    // so anything but the canonical shape means the column has been written by
    // something that did not go through ToKey, and silently accepting it is how
    // a second spelling gets a foothold.
    public static Guid FromKey(string key) => Guid.ParseExact(key, "N");

    // Wire id -> Guid, or null if it is not one. Lenient, and null for both
    // malformed and unknown ids on purpose: Subsonic answers those identically
    // (error 70), so there is nothing for a caller to tell apart.
    public static Guid? FromWire(string? id) => Guid.TryParse(id, out var parsed) ? parsed : null;
}
