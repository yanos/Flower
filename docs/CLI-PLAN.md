# Flower CLI — Implementation Plan

## Goal

Grow `Flower.CLI` from its current one-shot player script into a small,
genuinely useful command-line interface to the *same* library/playlist data
the desktop app uses (`~/Library/Application Support/Flower/library.json` and
`playlists.json`) — **not** a feature-complete port of the GUI. Anything that
only makes sense with a mouse/screen (tag editing, album art, drag-reorder,
column customization) is explicitly out of scope. See "Non-goals" below.

## Current state (starting point, not from scratch)

`Flower.CLI/Program.cs` already works and is worth preserving the shape of:
top-level-statements script, no Avalonia UI dependency (it talks to
`LibVLC`/`MediaPlayer` directly and to `Flower.Manager.VlcNativeSetup` for
native bootstrap — it does **not** go through `VlcAudioManager` or any
ViewModel). It already does:
- `flower-cli <file...>` — play explicit files.
- `flower-cli [query]` — load/scan the library via `LibraryStore`/`Importer`,
  optionally filter by a free-text query, and play the result.
- An interactive keyboard loop once playback starts: `space` pause/resume,
  `n` next, `q` quit.

This plan extends that pattern rather than replacing it.

---

## Interface shape: argv subcommands + the existing keyboard loop

Two shapes were considered:
- **Full interactive REPL** (`flower>` prompt, stateful session) — more
  powerful (build a queue, browse, then play, all in one process) but a much
  bigger lift: custom command parser, session state management, its own
  testing surface.
- **Argv subcommands, one process per invocation** (git-style) — matches
  what's already there. Anything that plays audio drops into the existing
  interactive keyboard-control loop for the duration of playback; anything
  that doesn't (search, list, playlist CRUD) prints and exits.

**Recommendation: the second one.** It's the natural extension of the
existing code, needs no new parsing dependency, and matches "not 100%
feature complete" — a REPL is the kind of scope creep this plan is
deliberately avoiding. Revisit only if the command surface grows enough that
users are clearly asking to stay in a session.

### Argument parsing

The verb count stays small (~5-6). Plan is a manual `switch` on `args[0]` in
`Program.cs`, same style as today — **not** pulling in `System.CommandLine`.
Reconsider only if the surface grows well past what's planned here.

### Terminal rendering: Spectre.Console

Add the `Spectre.Console` NuGet package to `Flower.CLI.csproj` rather than
hand-rolling table/box-drawing output. It's the standard choice for
"pretty" .NET CLI output and fits the argv-subcommand shape above (as
opposed to a full-screen TUI framework, which would only make sense if this
were the REPL shape instead — see below):
- `Table` for `search`, `playlist list`, `playlist show` output instead of
  manually padding/aligning columns with box-drawing characters.
- `AnsiConsole.Status()`/`Progress()` for `library rebuild`'s scan, replacing
  the current bare `Console.Write("Loading library...")`.
- `AnsiConsole.Live()` for a "Now Playing" panel that updates in place
  during the existing keyboard-control playback loop (track title/artist,
  elapsed time, pause state) instead of the current one-line-per-event
  `Console.WriteLine`.
- Styled/colored text (`[green]...[/]` markup) for errors ("no tracks
  matching"), instead of plain stderr text.

**Not chosen for v1: Terminal.Gui.** Terminal.Gui (v2) is the right tool if
this ever becomes a persistent full-screen TUI (library/playlist panels,
keyboard navigation between them, a permanent now-playing bar — closer to
`cmus`/`ncmpcpp`) rather than one-shot subcommands. That's a materially
bigger lift (session/window management, its own input handling separate
from the "read a key, act, loop" pattern used today) and would mean
reversing the "argv subcommands, not a REPL" decision above. Worth
revisiting if the subcommand interface ends up feeling like it wants to be a
persistent app, but not a v1 goal.

---

## Command surface (v1)

| Command | Behavior |
|---|---|
| `flower-cli play [query]` | Existing behavior, unchanged: load library, optionally filter, play, interactive controls. |
| `flower-cli play <file...>` | Existing file-mode playback, unchanged. |
| `flower-cli library rebuild` | Force a rescan (`Importer.Import()`) and persist, independent of playback. Currently rebuild only happens implicitly when the cache is empty. Progress shown via `AnsiConsole.Status()`. |
| `flower-cli library info` | Print track/album/artist counts and the resolved library path — a read-only sanity check, useful for confirming the CLI and GUI are pointed at the same data. |
| `flower-cli search <query>` | Print matching tracks (title — artist — album — duration) as a Spectre `Table`, without playing them. Reuses `TrackListBuilder.Build` (see Reuse section) so results match what the GUI would show. |
| `flower-cli playlist list` | List playlist names + track counts as a `Table`. |
| `flower-cli playlist show <name>` | Print a playlist's tracks in order as a `Table`. |
| `flower-cli playlist create <name> [query]` | Create a playlist; if `query` is given, seed it with matching tracks (skips the GUI's "New Playlist" + inline-rename dance entirely — the name is just a required argument). |
| `flower-cli playlist add <name> <query>` | Append matching tracks to an existing playlist. |
| `flower-cli playlist remove <name> <query>` | Remove matching tracks from a playlist. |
| `flower-cli playlist play <name>` | Build the queue from `Playlist.Tracks` in stored order and hand off to the existing playback loop. |

### Reuse points (don't reimplement)

- **Search/filter**: call `TrackListBuilder.Build(tracks, query, "Title", true)`
  and read `.Track` off each row, instead of writing a second filter
  implementation. This is the same code path the GUI's search box uses and
  is already unit-tested in `Flower.Tests`.
- **Persistence**: `LibraryStore`/`PlaylistStore`, unchanged, same as today
  — this is what makes CLI-created playlists show up in the GUI and vice
  versa. `PlaylistStore.Load(tracks)` needs the resolved library tracks, so
  every `playlist *` command loads the library first (same constraint the
  GUI has via `App.axaml.cs`).
- **Playback loop**: extract today's inline queue/`playNext`/keyboard-loop
  block out of `Program.cs` into `PlaybackSession.Run(List<Track> queue)` so
  `play` and `playlist play` share it instead of duplicating it. This is
  also where the Spectre.Console `Live` now-playing panel plugs in.

---

## Project structure

Current: one `Program.cs`. Planned split, once the command count above lands
(resist doing this before there's a second command — the current file is
still small enough to defer this):

```
Flower.CLI/
  Program.cs                 — parses args[0], dispatches, stays tiny
  Commands/
    LibraryCommands.cs        — rebuild, info
    SearchCommand.cs          — search
    PlaylistCommands.cs       — list, show, create, add, remove, play
  PlaybackSession.cs          — queue + keyboard loop, extracted from today's Program.cs
```

Deliberately flat — this is still a small tool, not a layered application.
No new abstractions (no command interface/registry) unless a concrete need
shows up; a `switch` in `Program.cs` calling static methods is enough for
~10 verbs.

`Flower.CLI.csproj` gains one new package reference: `Spectre.Console`.

---

## Explicit non-goals (why they're excluded)

- **Tag editing** — `TrackInfoWindow`'s edit flow is inherently a form-filling
  UI task; a CLI equivalent (flag-per-field) would be a large, low-value
  surface for a "not 100% complete" tool. Track info display (read-only) is
  in scope; editing is not.
- **Album art** — meaningless in a terminal (no image rendering).
- **Drag-to-reorder** — the GUI feature is pointer-drag-specific. A CLI
  equivalent (`playlist move <name> <from> <to>`) would be trivial to add
  later since `Playlist.Tracks` is a plain `List<Track>`, but it's not part
  of v1 — flagged as an easy, self-contained follow-up.
- **Playlist deletion** — the GUI itself doesn't have this yet either
  (`Library` has no `RemovePlaylist`); CLI shouldn't get ahead of the model.
  Small follow-up (`Library.RemovePlaylist(Playlist)` + `playlist delete`)
  once the GUI grows it, so both stay in sync.
- **Background rescan / watch mode** — the desktop app rescans on every
  launch in the background; the CLI is a short-lived process per invocation,
  so `library rebuild` (explicit, on-demand) is the right-sized equivalent.
- **Column/sort customization, sidebar navigation memory** — GUI-state
  concepts with no CLI analogue.

---

## Testing

- Anything pure-logic-extractable (e.g. "which tracks does a playlist
  `add`/`remove` query match", "build a queue from a playlist in stored
  order") should be pulled into small static methods and unit tested in the
  existing `Flower.Tests` project, following the pattern already established
  there (`TrackListBuilderTests`, `PlaylistTests`) — no new test project
  needed for a CLI this size.
- Actual playback (LibVLC) stays untested, consistent with the GUI today —
  there's no existing precedent for testing audio playback in this codebase,
  and it's not worth introducing one just for the CLI.
- `library`/`playlist` commands that touch `LibraryStore`/`PlaylistStore`
  should be tested the same isolated way `StoreRoundTripTests` already does
  (temp `HOME` redirection) if any new pure-logic pieces get pulled out of
  them.

---

## Suggested execution order

1. Extract `PlaybackSession` from today's `Program.cs` inline loop — no
   behavior change, just a refactor that both `play` and `playlist play`
   will depend on.
2. Add the `Spectre.Console` package reference and swap today's plain
   `Console.Write`/`Console.WriteLine` calls for it (status text, errors) —
   do this early so every command added afterward is written against it
   from the start instead of being retrofitted later.
3. Add `library rebuild` / `library info` — smallest, most self-contained
   commands, good first real subcommand to validate the dispatch shape and
   first use of `AnsiConsole.Status()`.
4. Add `search` (reusing `TrackListBuilder`, output via `Table`) — validates
   the "reuse GUI logic" approach before playlists build on the same
   pattern.
5. Add `playlist list` / `playlist show` (read-only, `Table` output) —
   exercises `PlaylistStore.Load` without any mutation risk yet.
6. Add `playlist create` / `add` / `remove` (mutating, calls
   `PlaylistStore.SaveAsync`) — do this after step 5 so read-path bugs are
   already shaken out.
7. Add `playlist play` — depends on step 1 (`PlaybackSession`) and step 5/6
   (playlist data access) both being done. Add the `Live` now-playing panel
   here, once there's a real playback session to attach it to.
8. Only then, if useful: `playlist move` (reorder) as the first stretch item
   from the non-goals list.
