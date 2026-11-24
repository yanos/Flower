# Playlist Sync Plan

Make a playlist edit impossible to lose, and stop asking people to settle
conflicts that are not conflicts. It replaces how playlists are identified,
versioned and merged between devices and `Flower.Server`.

**Status (November 2026): phases 1 and 2 are built.** Phase 1 fixed UF-18,
UF-20 and UF-24; phase 2 fixes UF-19, UF-21 and UF-22. What is left is phase 3,
UF-23 in [USER-FLOW-REVIEW.md](USER-FLOW-REVIEW.md).

## Why patching stops working

Recent fixes (UF-6, UF-7, UF-10, UF-17) were each correct and each worked around
one of three gaps. The same gaps produced UF-18 to UF-24.

| Gap today | What it causes | Findings |
|---|---|---|
| A playlist row has no lasting identity. It is a position holding a song, and a row whose song a device lacks is dropped from what it sends. | Songs lost through a device that could not see them; copies of a song confused | UF-18, UF-20, UF-24 |
| A version is a device's `UpdatedAt`, and the server takes whichever is newer. | Edits overwritten with nobody asked; agreement that drifts | UF-19, UF-21, UF-22 |
| A device remembers *when* it last agreed (`sync-state.json`), not *what* it agreed on. | Nothing to merge from, so any two edits become a prompt | UF-23 |

The workarounds are where the complexity now lives: the planner's "neither side
moved" and superset rules, `PlaylistSessionOverlay`, `AlignByOrder` and its
helpers, `KeepPlayingCopy`, and the queue's saved slots.

## The rules this has to keep

1. **A row leaves a playlist only because someone removed it.** Not because a
   device could not resolve its song, and not because of a failed or stale sync.
2. **Nothing is overwritten unseen.** A write based on an old version is refused.
3. **Ask only about a real disagreement:** both sides changed the same thing to
   different values.
4. **A restored server backup loses nothing** (UF-6). A playlist merely missing
   from the server is pushed back, and only a recorded deletion deletes.
5. **Edit beats delete.** A playlist edited on one device and deleted on another
   survives.
6. **A push speaks for one listener.** Refusing another listener's playlist stays
   exactly as it is (`docs/SECURITY.md`).
7. **The listener's phone stays simple.** All of this is invisible unless there is
   a real disagreement.

## The model

### Entries

A playlist is an ordered list of entries:

```text
PlaylistEntry
  EntryId   Guid, minted by the device that added the row, never reused
  Song      title, artists, album, seconds, origin id (today's PlaylistSyncTrackDto)
  Track?    the library's track, when this device has it
```

`Playlist.Tracks` stays as the entries that have a track, so most readers do not
change. An entry without one is hidden and unplayable, kept in its place, sent
in every push, and becomes playable when the library gains the song. This replaces
`AbsentEntry` and the in-memory `StoredEntry` keys.

`playlist_tracks` gains `entry_id` and the song fields, and `track_id` becomes
nullable. A queue row holds the `EntryId` it came from, saved in `queue.json` in
place of `SourceSlots`.

### Revisions

The server is the only thing that issues versions, so device clocks stop
mattering. Each accepted write gets:

```text
revision = max(last issued + 1, server's UTC milliseconds)
```

Taking the clock into account keeps numbers unique after a backup is restored,
when a plain counter would reissue ones devices already hold. The server keeps
each playlist's last 64 revisions, and a deletion carries a revision too.

`UpdatedAt` stays for display and sorting ("last changed first"), and as the
tie-break for a smart or Vibe query. It no longer decides what a sync does.

### The agreed copy

A device stores, per server and playlist, the revision it last agreed on and a
snapshot of that version, in `flower.db` beside the playlists so that they cannot
disagree after a crash. This replaces `sync-state.json`. "Did I change this?" is
a comparison with the snapshot, so an edit and its undo is no change at all.

## The protocol

There is no compatibility to keep, so this replaces the current routes outright.

**`GET /api/flower/v1/playlists`** answers each playlist with `Revision`,
`History` (its recent revisions) and its rows, and each recorded deletion with
its `Revision` and `History`.

**`POST /api/flower/v1/playlists/changes`** replaces `/playlists/apply`. It is one
request per session, because the bulk budget is 60 requests a minute.

```text
Changes: [{ Id, BaseRevision, Playlist | Delete }]
Answer:  [{ Id, Applied(Revision) | Stale(current copy) | Refused }]
```

Each playlist succeeds or fails on its own. `BaseRevision` 0 means "new". An
answer lost in transit is harmless: the next session finds the server's copy equal
to this device's and records the revision.

How a device reads the server's version against its own base:

| Server's version | Meaning | Action |
|---|---|---|
| Equal to base | Server unchanged | Push if this device changed |
| Base is in `History` | Server moved on | Merge |
| Base is newer and not in `History` | Server was restored from a backup | This device's copy goes back up |
| No base, or unrelated | No shared history | Same content: agree. Otherwise ask |
| Missing, no recorded deletion | Server lost it | Push it back |
| Recorded deletion, base in its `History` | Deleted elsewhere | Unchanged here: delete. Changed here: keep the edit |
| Recorded deletion, no base | Cannot tell whether this copy holds an edit | Ask once: "Deleted on your server. Keep it?" |

## Merging

A three-way merge of the agreed copy, this device and the server, field by field:

- **Name, folder, place in the folder:** one side changed it, take that. Both
  changed it to different values, ask.
- **Entries:** additions and removals from both sides always combine, by `EntryId`.
  One side reordering combines with the other's additions and removals. Both
  reordering differently is the one case that asks.
- **Smart rules, Vibe sentence:** one side changed it, take that. Both did, the
  newer `UpdatedAt` wins with no prompt, as today, since no hand-built list is at
  stake. Their songs are never compared.

`PlaylistThreeWayMerge` already has this shape and moves from occurrence counting
to `EntryId`.

A session no longer waits on a person. It reads, merges under the library's lock
against the playlists as they are at that moment, pushes, and on `Stale` merges
and retries, at most three times. Real disagreements are collected and asked about
after the session ends. The answer is an ordinary local edit on top of the server's
revision, which the next session pushes.

## Phases

Each phase starts with its findings as failing scenarios and is its own commit.
Client and server always move together. So does the browser head, which has its
own path to these routes: `OriginPlaylistImporter` reads and `OriginPlaylistWriter`
pushes, without going through `PlaylistSyncService`.

### Phase 1: entries (UF-18, UF-20, UF-24)

- `PlaylistEntry`, the schema change and its load and save.
- Entries on the wire, kept by client and server whether or not they resolve.
  Includes `PlaylistSyncMapper`, `OriginPlaylistImporter`, `OriginPlaylistWriter`.
- `PlaylistThreeWayMerge` on `EntryId`.
- `QueueFollowsPlaylist` on `EntryId`. An edit from another device then says
  exactly which copy changed, which closes UF-17's remaining best guess.
- The "2 songs unavailable" line on the desktop and phone playlist screens.

**Deleted:** `AlignByOrder`, `TryAlignMove`, `TryAlignInOrder`, `KeepPlayingCopy`,
`AbsentEntry`, `StoredEntry`.

What building it changed from the above:

- **Rows stay in the wire's `Tracks` field,** each with an `EntryId`, rather than
  moving to a new `Entries` field.
- **`OriginsIn` and `OriginsFit` stay,** on `EntryId`. A queue is still built from
  a list of songs, so which row each came from is worked out when it is made.
- **Copies from before rows had ids** hold the same songs under different ids. A
  device seeing the same songs takes the server's ids with no edit
  (`TakeEntryIdsFrom`). A copy sharing no ids is paired by song, and does not
  remove rows it could not have known about (`KeepUnavailableFrom`).
- **Both sides adding the same song** during a session is one addition, as it was.

### Phase 2: revisions (UF-19, UF-21, UF-22) - built

Server revisions with history and revisioned deletions (`PlaylistRevisions`,
`PlaylistRevisionRepository`), `POST /playlists/changes`, the agreed copy in
`flower.db` (`PlaylistAgreementRepository`), the read table in
`PlaylistSyncPlanner`, and a session that never waits on a prompt.

**Deleted:** `PlaylistSyncStateStore` (`sync-state.json`), `Supersedes`,
`ApplyPushedManifest`, `HoldsAllAndMore`, the "neither side moved" branch,
`/playlists/apply`, `PlaylistDeletionRepository` (`deleted_playlists`).

What building it changed from the above:

- **The server issues a revision whenever a playlist's content changes**, by
  listening to `Library.PlaylistsChanged` and comparing with the version it
  last issued (`PlaylistVersion`: name, folder, place, rules, sentence, and the
  rows by `EntryId`; not `UpdatedAt`, nor a smart or Vibe playlist's songs).
  `/changes` is the only writer today, but an edit that reached the library
  without a revision would be overwritten by the next device to write over
  the old one. Started after the stored playlists load, which also mends a
  crash between saving a playlist and recording its revision.
- **The agreed copy is saved after the install, not in its transaction.** In
  that order a crash leaves a playlist that already matches the server against
  an older agreement, which the next session reads as the same content and
  agrees on. The other order would read as an edit and push it.
- **A session's questions are asked after it ends.** An answer is applied as
  an edit over the server's revision the question showed (keeping this copy
  agrees on the server's; keeping the server's installs it), only if this
  device's copy is still the one asked about, and a follow-up session sends it.
  "Keep it" over a deletion agrees on the deletion, as a copy-less agreement.
- **A refused change is recorded as deleted for the caller's listener over the
  revision it named**, and the device agrees at that revision. Its next `GET`
  then shows a deletion over a version it agreed on, which drops its copy
  without a prompt, and without trusting the unsigned answer.
- **A browser tab writes each changed playlist over the revision it read**;
  a `Stale` answer is left for the next refresh, since a tab does not merge.
- **An edit made between a session's read and its plan is now a both-sides
  edit**, asked about rather than merged: the prompt no longer opens a window
  in the middle of a session, so the old "edited while the session takes the
  server's edit keeps both" scenario is phase 3's (UF-23).

### Phase 3: merge by default (UF-23)

- The planner merges wherever the table says to, and asks only about a real
  disagreement. The desktop window and phone sheet stay, with fewer reasons to appear.

**Deleted:** `PlaylistSessionOverlay`, once merging against the current playlists
covers edits made during a session.

## Testing

- **Scenarios:** one per finding and per row of the read table, in
  `Tests/Flower.Tests/Sync/Scenarios`, against `SimulatedFlowerServer`, which keeps
  calling the server's own apply rule.
- **Randomised convergence, the main defence against new edge cases:** three
  devices make seeded random edits, syncs, failed pushes, lost answers and restores.
  Once all have synced, every device must hold the same playlist, and no entry may
  be missing unless some device removed it. A failure prints its seed.
- **Server:** revisions, stale writes and listener refusal in
  `Tests/Flower.Server.Tests/Endpoints/SyncEndpointTests.cs`.
- **End to end:** two real devices against a throwaway server before each phase is
  committed. On a Mac, start it with `--Flower:IntegrateWithITunes=false`, or it
  opens Music.app and imports the real library.

## Docs to update as it lands

`API.md` (routes), `SYNC-PLAN.md` (playlist sync), `SECURITY.md` (the new write
route's listener check), `USER-FLOW-REVIEW.md` (each fixed finding leaves), and
`MANAGING-SONGS.md` and `GETTING-STARTED.md` (the "songs unavailable" line).

## Decisions

1. **Decided: a device has a playlist the server records as deleted, and no agreed
   copy.** It cannot tell whether it holds an edit, so it asks once, "Deleted on
   your server. Keep it?", through the existing conflict screens.
2. **Decided: entries this device cannot play or stream.** A song on the server is
   never one of these, downloaded or not, because a paired device lists the whole
   catalog. These are songs the server does not have (only on another device),
   songs on a drive that is away, and, briefly, everything before a new device's
   first catalog arrives. They stay hidden, as absent songs are now, and the
   playlist shows a quiet "2 songs unavailable" once the catalog has arrived, so a
   shorter list does not look like lost music.
