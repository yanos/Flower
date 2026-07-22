# Flower CLI — Implementation Plan

Grow `Flower.CLI` from its current one-shot player script into a small, useful command-line interface to the same library/playlist data the desktop app uses (`library.json`/`playlists.json`) — not a feature-complete GUI port. Not yet started.

## Current state

`Flower.CLI/Program.cs` talks to `LibVLC`/`VlcNativeSetup` directly (not `VlcAudioManager`/ViewModels). Already supports: `flower-cli <file...>` (play explicit files), `flower-cli [query]` (load library, filter, play), and an interactive keyboard loop (`space`/`n`/`q`). This plan extends that pattern rather than replacing it.

## Interface shape: argv subcommands, not a REPL

Chose git-style one-process-per-invocation subcommands over a stateful REPL — matches the existing code, needs no new parsing dependency. Playing commands drop into the existing keyboard loop; others print and exit. Argument parsing stays a manual `switch` on `args[0]`, not `System.CommandLine` — revisit only if the verb count grows well past ~6.

**Terminal rendering: add `Spectre.Console`** for tables (`search`, `playlist list/show`), status/progress (`library rebuild`), and a `Live` now-playing panel — fits the subcommand shape. **Not `Terminal.Gui`** — that's the right tool only for a persistent full-screen TUI, which would reverse the subcommand decision; not a v1 goal.

## Command surface (v1)

| Command | Behavior |
|---|---|
| `play [query]` / `play <file...>` | Existing behavior, unchanged. |
| `library rebuild` | Force rescan + persist, with `AnsiConsole.Status()` progress. |
| `library info` | Print track/album/artist counts + resolved library path. |
| `search <query>` | Print matches as a table, reusing `TrackListBuilder.Build` — no second filter implementation. |
| `playlist list` / `show <name>` | Table output. |
| `playlist create <name> [query]` / `add <name> <query>` / `remove <name> <query>` | Mutating, via `PlaylistStore`. |
| `playlist play <name>` | Build queue from stored order, hand off to the playback loop. |

Reuse `TrackListBuilder.Build` for search, `LibraryStore`/`PlaylistStore` unchanged for persistence (keeps CLI/GUI data in sync), and extract today's inline queue/keyboard-loop code into `PlaybackSession.Run(List<Track> queue)` so `play` and `playlist play` share it.

## Project structure

Split once the command count above lands (not before — current file is still small enough):
```
Flower.CLI/
  Program.cs          — parses args[0], dispatches
  Commands/           — LibraryCommands, SearchCommand, PlaylistCommands
  PlaybackSession.cs  — queue + keyboard loop
```
Deliberately flat, no command interface/registry abstraction for ~10 verbs. Adds one package reference: `Spectre.Console`.

## Non-goals

Tag editing (form-filling UI task), album art (no terminal rendering), drag-to-reorder (GUI-specific; `playlist move` would be trivial later but isn't v1), playlist deletion (GUI doesn't have it yet either — add both together later), background rescan/watch mode (`library rebuild` is the right-sized on-demand equivalent), column/sort/sidebar state (no CLI analogue).

## Testing

Pull pure logic (playlist query matching, queue building) into static methods and unit test in `Flower.Tests`, following existing patterns (`TrackListBuilderTests`, `PlaylistTests`). Actual LibVLC playback stays untested, consistent with the GUI. Store-touching commands use the same temp-`HOME` isolation as `StoreRoundTripTests`.

## Suggested order

1. Extract `PlaybackSession` (pure refactor, no behavior change).
2. Add `Spectre.Console`, convert existing output early.
3. `library rebuild`/`info` — smallest, validates dispatch shape.
4. `search` — validates the "reuse GUI logic" approach.
5. `playlist list`/`show` — read-only.
6. `playlist create`/`add`/`remove` — mutating.
7. `playlist play` — depends on steps 1 and 5/6; add the `Live` panel here.
8. Stretch: `playlist move`.
