# User-flow review

Bugs a person using Flower would hit: data lost, the wrong thing done, a dead
end, or behaviour that misleads. The `user-flow-review` skill finds them and
keeps this list; `/release` treats every open **P0** here as blocking the tag.

**Status:** 14 open (5 P0, 4 fix-now, 5 later), from the review of `ef6695fb`
on 2026-11-22.

Severities are the ones in `.claude/skills/release/REVIEW.md`. **Reproduced**
means a throwaway test against an export of that commit went red on the bug;
**read** means the finding comes from tracing the code. No finding was checked
through the real UI, or on Windows, Linux or a phone. Line numbers are as of
that commit.

An id is never reused. A fixed finding leaves this list, and its commit records
the fix.

## P0: data is lost or damaged

**UF-3. A listener's own file can be overwritten by the server's version.** *Reproduced.*
A listener's own rip, matched to a server song only by its tags, is replaced
without going to Trash when the owner uploads a new version. A different format
ends up under the old extension, and the swap happens before the new file is
checked as readable.
`Flower/Services/LibrarySyncService.cs:664`, `:674`.
*Direction:* replace only downloads and an owner's copies, never across formats,
and validate before the swap.

**UF-4. A full music disk corrupts a song during replacement.** *Reproduced.*
Uploads are staged on the data volume, so moving one into the music folder is a
copy. When it runs out of space, a truncated file stays at the song's path and
the good original is stranded in a hidden `.flower-replaced-*` file. A new
upload that fails the same way leaves a partial file that later reads as a
conflict.
`Flower.Core/Services/LibraryIngest.cs:297`, `:334`.
*Direction:* copy to a temporary name beside the target, rename, and always
restore the original on failure.

**UF-5. Desktop can delete the only remaining copy without asking.** *Reproduced for unpairing; read for a song the server stopped listing.*
A download keeps `IsLocallyDownloaded` after its server is gone, so Delete Local
File skips the warning and bypasses Trash. The phone asks the right question
(does the paired server still have it).
`Flower/Services/LocalFileDeletion.cs:17`, `Flower.Core/Models/Library.cs:1511`.
*Direction:* share the phone's rule, and use the Trash.

**UF-6. Restoring a server backup deletes newer playlists on every device.** *Read.*
A playlist created after the backup is missing on the server and unchanged since
the last sync, which the planner reads as deleted there.
`Flower/Services/PlaylistSyncPlanner.cs:78`.
*Direction:* delete only on an explicit record of a deletion, or detect that the
server's state went backwards.

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

**UF-10. Removed songs stay in the current queue.** *Read.*
They keep playing until relaunch, or fail when reached if their file was trashed.
`Flower/ViewModels/PlaylistControlViewModel.cs:1010`.
*Direction:* take removed songs out of the live queue.

**UF-11. Delete Local File leaves a dead row on an unpaired library.** *Read.*
The song stays listed with nothing to play and no source to get it from.
`Flower/Services/LibraryDownloadService.cs:236`.
*Direction:* a song with no other source leaves the library with its file.

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
