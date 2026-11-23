# Flows, setups and lenses

What [SKILL.md](SKILL.md) walks. Add to this file whenever a real bug came from a
flow or a question it lacks.

## Setups

Each one sees different menus and owns different things. Walk a flow under every
setup it can happen in.

- **Unpaired desktop:** library folders, its own files.
- **Owner's desktop:** a paired admin with files of its own, some also on the server.
- **Admin phone:** paired, mostly streaming, a few downloads.
- **Listener:** paired without admin, possibly with files of its own that match
  the server's songs.
- **Browser tab:** the server's catalog, nothing kept across a refresh.
- **The server:** Docker, with music on a separate and possibly absent mount.

## Lenses

Put each question to each step of a flow.

- **Relaunch.** Does it survive one launch, and a second? State loaded as a subset
  and then saved back is how data disappears a launch late.
- **Absence.** Drive unplugged, NAS unmounted, server down, offline, permission
  revoked. Is "not there now" treated as "gone"?
- **Interruption.** App killed or backgrounded mid-write, connection lost
  mid-transfer, disk full, read-only folder, a move across volumes.
- **Round trip.** Does a field the model or wire does not carry come back blank
  and get written over the real value?
- **Identity.** Same tags but a different file, the same song twice in a list, a
  case-only rename, non-ASCII names, the same song in two formats.
- **Ownership.** Whose file, song or playlist is this, and may this device touch it?
- **Concurrency.** The same thing edited on two devices, an edit during a sync or
  scan, clocks that disagree, a server restored from an older backup.
- **Truth.** Does the prompt describe what happens (Trash or gone for good, here or
  everywhere)? What does dismissing it choose? Does success show after a failure?
- **Parity.** Does the same action behave the same on desktop, phone and browser,
  with the same warnings?
- **Way back.** Can it be undone? Can the person get the thing back the obvious way,
  or are they left with a row that cannot be played or removed?
- **Feedback.** Does a failure reach the screen, or only the log? Does an action
  that did nothing say why?
- **Scale.** A five-figure library, a select-all, a long queue: does it block the UI
  thread?
- **Input.** Keyboard focus and shortcuts while typing, right-click on a row outside
  the selection, double activation, a list rebuilt during a gesture.

## Data flows

Walked on every run.

- **Adding songs:** first launch, library folders, drop (file, folder, repeat
  drop, name collision), Music.app import, the folder watcher.
- **Removing songs:** Remove from Library with and without files, Delete Local
  File, Removed Songs (restore, delete for good), deleting in Finder, removing a
  library folder, a removal arriving from another device.
- **Editing songs:** Track Info for one song, several, Prev/Next, from Now Playing,
  on a streamed song; artwork; the compilation flag; an edit reaching other devices.
- **Files changing underneath:** external rename, move or retag, a new version
  replacing a file, upload to the server, a server-side move followed by devices.
- **Playlists:** create, rename, delete, folders, duplicates, reorder, smart and
  Vibe playlists, a song leaving the library, conflicts, sync in both directions.
- **Pairing:** pair by link and code, first sync with overlapping libraries, unpair,
  re-pair, switch server, trust revoked, promoted or demoted admin, phone reinstall,
  server restored from backup.
- **Downloads:** song, album and playlist, interrupted and resumed, removed, the
  server's copy changing or leaving, playing offline.

## Everyday flows

Walked when in scope.

- **Playback and queue:** play from each list, next and previous at both ends,
  shuffle, repeat, gapless, Play Next, reorder, clear, the queue across relaunch, a
  queued song leaving the library, an unplayable song, output device changes, phone
  interruptions, media keys, AirPlay and Bluetooth.
- **Browsing:** search, filter, sort, selection across rebuilds, what a menu acts on,
  Go to Album and Artist, scroll position, empty states.
- **Home and history:** Continue Playing, Recently Played and Added, play counts and
  stars across devices, resume positions.
- **Settings:** each setting persists, applies without a restart, and is honest
  about who it affects; the EQ; server settings edited from a device.
- **Running a server:** first start and first admin code, inviting a listener, the
  web UI, a rescan during playback, remote access, updating, backing up and restoring.
- **Installing and updating:** first run with no data, an update over an existing
  library, a listener's path from a link to playing music.
