# User-flow review

Bugs a person using Flower would hit: data lost, the wrong thing done, a dead
end, or behaviour that misleads. The `user-flow-review` skill finds them and
keeps this list; `/release` treats every open **P0** here as blocking the tag.

**Status:** 8 open (1 P0, 2 fix-now, 5 later), from the review of `ef6695fb`
on 2026-11-22.

Severities are the ones in `.claude/skills/release/REVIEW.md`. **Reproduced**
means a throwaway test against an export of that commit went red on the bug;
**read** means the finding comes from tracing the code. No finding was checked
through the real UI, or on Windows, Linux or a phone. Line numbers are as of
that commit.

An id is never reused. A fixed finding leaves this list, and its commit records
the fix.

## P0: data is lost or damaged

**UF-7. Playlist conflicts can silently discard edits.** *Read.*
Phones have no conflict screen, so they always keep their own version, stamp it
newer and push it over the other device's edits. Closing the desktop conflict
window with its close button chooses the same.
`Flower/ViewModels/MainViewModel.cs:2177`, `Flower/Views/MainView.axaml.cs:2199`.
*Direction:* a conflict screen on phones, and a dismissed prompt leaves the
conflict unresolved.

## Fix-now: the app does the wrong thing

**UF-8. Duplicate playlist rows act on the wrong copy.** *Reproduced.*
Removing row 3 of `A, B, A` leaves `B, A`, and dragging the last row of
`A, B, C, A` above `B` does nothing, because both operations find the song
rather than the row. `Flower.Core/Models/Palylist.cs:336`, `:379`.
*Direction:* address entries by slot, as the queue does.

**UF-9. Failed tag saves look successful.** *Reproduced for the streaming case; read for the rest.*
Track Info closes normally when a file could not be written. Cmd/Ctrl+I on a
streaming song with no row selected always fails this way, because it edits the
temporary stream copy.
`Flower/Services/TrackTagFields.cs:166`, `Flower/Views/TrackInfoWindow.axaml.cs:222`,
`Flower/Views/MainView.axaml.cs:2029`.
*Direction:* say what failed and keep the window open; resolve to the library's track.

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
