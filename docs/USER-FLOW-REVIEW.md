# User-flow review

Bugs a person using Flower would hit: data lost, the wrong thing done, a dead
end, or behaviour that misleads. The `user-flow-review` skill finds them and
keeps this list; `/release` treats every open **P0** here as blocking the tag.

**Status:** 9 open (1 P0, 0 fix-now, 8 later). UF-12 to UF-16 are from the
review of `ef6695fb` on 2026-11-22. UF-19 and UF-21 to UF-23 are from a run of
paired devices against a real `Flower.Server` at `99c0173d` on 2026-11-23, and
[PLAYLIST-SYNC-PLAN.md](PLAYLIST-SYNC-PLAN.md) phases 2 and 3 are their fix.

Severities are the ones in `.claude/skills/release/REVIEW.md`. **Reproduced**
means a throwaway test against an export of that commit went red on the bug, or,
from UF-19 on, a throwaway harness did, driving the real sync services against a
real server. **Read** means the finding comes from tracing the code. No finding
was checked through the real UI, or on Windows, Linux or a phone. Line numbers
are as of the commit named for that finding.

An id is never reused. A fixed finding leaves this list, and its commit records
the fix.

## P0: data lost

**UF-19. An edit can vanish while a conflict prompt is open.** *Reproduced.*
A session holds what it read from the server for as long as a prompt waits, then
pushes it, and the server takes whichever copy has the newer timestamp. Device A
added a song to a playlist and opened a prompt about a different one. Device B
added another song to A's playlist meanwhile. Both ended with B's song only, and
nobody was asked. `Flower/Services/PlaylistSyncService.cs:430`,
`Flower.Core/Services/PlaylistSyncMapper.cs:206`.
*Direction:* the server refuses a push based on an old revision (plan, phase 2).

## Later: confusing, or impossible to do

**UF-12. Dragging a removed song back in silently does nothing.** *Read.*
The only way back is Settings' Removed Songs, and nothing says so.
`Flower.Core/Services/LibraryFolderWatcher.cs:351`.
*Direction:* an explicit drop restores the song.

**UF-13. A retagged song can be ignored when dropped.** *Read.*
The same name and size counts as already there, and failed copies are only logged.
`Flower/Services/LibraryDrop.cs:115`, `Flower/ViewModels/MainViewModel.cs:3003`.
*Direction:* compare content, and report what was not added.

**UF-14. A field with mixed values cannot be cleared across several songs.** *Read.*
An empty box means "leave unchanged", so there is no way to say "make this empty".
`Flower/ViewModels/TrackInfoField.cs:39`.
*Direction:* an explicit clear action on a mixed field.

**UF-15. Dragging in a playlist is limited.** *Read.*
A multi-selection moves only the grabbed row, and songs cannot be dragged from
one playlist onto another. `Flower/Controls/MusicListView.axaml.cs:579`.
*Direction:* move the whole selection, and tell a reorder from a drag to the sidebar.

**UF-16. Adding a large selection freezes the app.** *Reproduced.*
Adding 5,000 songs took 22 seconds, because each one rewrites every playlist on
the UI thread. `Flower/ViewModels/PlaylistManagementViewModel.cs:538`.
*Direction:* append the batch as one edit.

**UF-21. A deleted playlist comes back from a device with no baseline for it.** *Reproduced.*
A device holding a playlist the server records as deleted, with neither
`sync-state.json` nor its `.bak` naming it, reads it as new and pushes it. The
server forgets the deletion and every device takes the playlist back.
`Flower/Services/PlaylistSyncPlanner.cs:87`.
*Direction:* deletions carry a revision, and a device with no base asks (plan, phase 2).

**UF-22. Undoing an edit makes another device's next edit a conflict.** *Reproduced.*
Adding a song and removing it again leaves the same songs at a newer timestamp. The
other device sees equal content and records its own older timestamp as agreed, so
its next edit reads as both sides having changed.
`Flower/Services/PlaylistSyncService.cs:274`.
*Direction:* compare against the agreed revision and content (plan, phase 2).

**UF-23. Edits that do not overlap still ask which version to keep.** *Reproduced.*
A rename on one device and a song added on another raise a conflict, because a
device remembers when it last agreed and not what it agreed on, so it has nothing
to merge from. `Flower/Services/PlaylistSyncPlanner.cs:202`.
*Direction:* keep the agreed copy and merge from it (plan, phase 3).
