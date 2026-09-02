# Smart Playlist Plan

Rule-based, self-updating playlists — the iTunes "Smart Playlist" feature.
A playlist defined by a query ("genre is Jazz, added in the last 30 days,
limit 25 by least recently played") that re-evaluates itself instead of
holding a hand-picked list.

**Phases 1 (the engine), 2 (persistence), 3 (recomputation) and 4 (UI) are
built and tested; phase 5 (sync + server) is not started.**
The rest is the design, and the reasoning behind the parts that were not
obvious.

Out of scope: seed-based "radio"/auto-DJ. That reading of "intelligent
playlist" shares nothing with this one but the `Track` model — see
"Adjacent, deliberately separate" at the end.

## The core decision: rules are the state, tracks are a cache

The obvious shape is `SmartPlaylist : Playlist` with a computed `Tracks`.
Don't. `Playlist` is load-bearing for three consumers that all assume a
stored list:

- the UI and playback, which read `Tracks`;
- `PlaylistSyncPlanner`, which decides "did this side change?" purely from
  `UpdatedAt` against a per-peer baseline;
- the Subsonic surface (`SubsonicEndpoints`), where a smart playlist has no
  representation at all — the protocol only has playlists with members.

Instead, `Playlist` gains one nullable property:

```csharp
public SmartPlaylistRules? Rules { get; set; }   // null => an ordinary playlist
```

`_tracks` stays exactly what it is: the materialized result. Every existing
reader keeps working untouched — sidebar, playback, `PlaylistRepository`,
the Subsonic endpoints, the track shipping half of sync. Only the write
path is new.

**Materialization must not `Touch()`.** There is already precedent for a
mutation that deliberately isn't one: `Playlist.RebindTracks` swaps entries
for the instances a rescan replaced without bumping `UpdatedAt`, because
sync reads only that field and a rebind is not an edit. Re-evaluating a
smart playlist is the same category — the songs changed because the library
changed, not because a user did anything. So re-evaluation goes through a
sibling of `RebindTracks`, and `UpdatedAt` keeps meaning "someone edited the
rules or the name".

The payoff is that a smart playlist becomes structurally incapable of a
content conflict.

## Rule model

Lives in `Flower.Core` so the server shares it rather than growing a twin.

```csharp
// Flower.Core/Models/SmartPlaylistRules.cs
public sealed record SmartPlaylistRules(
    MatchMode Mode,                            // All | Any
    IReadOnlyList<SmartCondition> Conditions,
    SmartLimit? Limit,
    bool LiveUpdating);

public sealed record SmartCondition(SmartField Field, SmartOperator Op, SmartValue Value);

public sealed record SmartLimit(int Amount, LimitUnit Unit, LimitSelector SelectedBy);
```

Nesting (a condition that is itself a group) is worth designing the record
for and not worth building the editor for on day one — flat All/Any covers
the overwhelming majority of real smart playlists.

**One field registry is the single source of truth**: `SmartField` (enum) →
display name, value type, and a `Func<Track, object?>` accessor. Everything
— the editor's field dropdown, which operators are offered, the evaluator,
JSON round-tripping — reads that one table. This is the same discipline
`Schema.cs`'s `album_artist` comment records: one expression, written once,
rather than a C# copy and a SQL copy that drift.

Fields for the first cut, grouped by value type:

| Type | Fields |
|---|---|
| Text | Title, Artists, AlbumArtist, Album, Genre, Composer, Grouping, Comment, Publisher |
| Number | Year, BeatsPerMinute, PlayCount, TrackNumber, DiscNumber, Bitrate, SampleRate |
| Duration | Duration |
| Date | DateAdded, LastPlayedAt, StarredAt |
| Bool | Starred, IsCompilation, IsLocallyDownloaded, IgnoreWhenShuffling |
| Playlist | is / is not in playlist X |

Text comparison goes through `SearchText`, the same accent- and
case-insensitive path the search box uses (`TrackListBuilder.Filter`) — two
different answers to "does this track match 'Bjork'?" in one app is a bug
waiting to be filed.

### Relative dates must stay relative

"Added in the last 30 days" stored as an absolute instant rots silently: it
keeps matching the same window forever, and nobody notices until the
playlist is a year stale. Store the offset (`30`, `Days`, `Within`) and
resolve it against an injected clock at evaluation time. The injected clock
is also what makes the date rules testable without sleeping.

### Evaluate in memory, do not build a SQL translator

`Library` is fully resident. A 16k-track library × a dozen playlists × a
compiled predicate is microseconds, and the deployment model (CLAUDE.md,
"How It Gets Used") is one owner and single-digit listeners. A rules→SQL
translator would be a second implementation of every operator, with its own
NULL and collation semantics, for no measurable gain. `SmartPlaylistEvaluator`
takes `IReadOnlyList<Track>` and returns `List<Track>`, and that is all.

## Membership rules, ordering, and cycles

A membership rule lets one smart playlist refer to another:

- "Fresh Rock" = genre is Rock **and** is not in "Already Heard"
- "Already Heard" = play count > 0

So Already Heard has to be evaluated before Fresh Rock. Generalized: build
the dependency graph and evaluate in topological order, leaves first.

The failure case is a cycle — A = "is not in B", B = "is not in A". There is
no valid order and no correct answer; evaluating it either loops or produces
whichever result depended on which one happened to run first, and flips on
the next recompute. Self-reference (A = "is not in A") is the one-node
version.

**Reject it at edit time, not evaluation time.** When the editor populates
the playlist dropdown for a membership rule, exclude every playlist that
already depends — directly or transitively — on the playlist being edited.
The bad state is then unrepresentable, and the evaluator never needs a cycle
path at all. The evaluator should still fail loudly rather than loop if it
ever sees one, as a guard against a hand-edited database or a rules blob
arriving from a peer.

A membership rule naming a playlist this device does not have resolves to
empty rather than failing the whole rule — matching how `playlist_tracks`
already tolerates track ids that no longer resolve.

## Recomputation

Triggers. Note that `Library.TracksUpdated` alone is *not* enough — the
fields the flagship playlists are built on deliberately do not raise it:

- `Library.TracksUpdated` — rescan, download, track added or removed. Covers
  the descriptive fields plus `DateAdded`.
- `Library.TrackStatsChanged` — play count, `LastPlayedAt`, skip count. These
  were split out of `TracksUpdated` precisely because it means a full
  track-list rebuild plus a peer sync, twice per track change
  (`ARCHITECTURE-REVIEW.md` Tier 1.1). But "Recently Played" / "Most Played" /
  "Never Played" live entirely on this event; hang recomputation off
  `TracksUpdated` only and they never update until a rescan.
- `Library.TrackStarsChanged` — a star, from the Track Info window or a
  Subsonic `/star`. Both go through `Library.SetStarred`, which reached neither
  event above; this event was added for exactly that. Deliberately not folded
  into `TrackStatsChanged`, which at least one subscriber reads as "a play
  happened" and forwards as a scrobble (`IPlayReporter`).
- `Library.PlaylistsChanged` — a `PlaylistRef` rule makes another playlist's
  contents an input, so a playlist change is a track-set change for its
  dependents.
- a rule edit, obviously — the editor calling `Schedule` directly.

Two paths that looked like they needed their own trigger turned out not to:
`MergeReportedTrackState` (an admin client pushing play counts and stars *in*)
already raises `TrackStatsChanged` per changed track, and `MergeSyncedTracks`
(a catalog pull) already raises `TracksUpdated`. Smart-playlist inputs arriving
from off-machine are covered by the same two events as this device's own
listening.

Debouncing is not polish: `TrackStatsChanged` fires twice per track change,
and a sync merge touches thousands of tracks in a loop.

One debounced pass recomputes every smart playlist in topological order,
rather than each playlist reacting independently — cheap, and it makes the
ordering requirement above a property of a single function instead of an
emergent one.

`LiveUpdating = false` means "evaluate once when saved, then freeze", the
same as iTunes.

### Both ends bake from the same recipe

**Settled:** a recompute is never a sync-visible change, including one
triggered by an incoming sync merge. Devices exchange rules, never computed
track lists.

The rules are small and already sync, because editing them is a real user
action. The inputs — play counts, stars, dates — already sync too. So both
ends reach the same answer from the same starting point without shipping the
answer itself, and a device that briefly disagrees converges on its next pass
because it is re-reading the truth rather than reconciling a stale copy.

Shipping the computed list instead would have each side recompute after a
merge, see a changed playlist, and push a near-identical list back — an echo
between two sides that agree, with a real disagreement window wherever
"in the last 7 days" straddles a clock difference.

This is already the behaviour rather than work to do: `Playlist.Materialize`
does not `Touch()`, and `PlaylistSyncPlanner` decides what to send from
`UpdatedAt` alone, so a recompute leaves no fingerprint for it to find. The
decision is to keep relying on that, and to treat any future need to
`Touch()` on materialization as breaking this property.

The cost is a `PlaylistRef` rule whose referenced playlist is stale on one
side: the two can show different contents until the next pass. That is a
better failure than two devices contending for ownership of a list.

Recomputation cannot disturb playback: `MainPlaylist` is a separate
`Playlist` and both its constructor and `ReplaceAll` take defensive copies,
so the queue built from a smart playlist is already detached from it.

## Persistence

Schema **V6**, appended as its own step — never by editing a released
migration. (V5 exists precisely because a column was folded into an
already-stamped V4 and never reached the databases that had been stamped.)

```sql
ALTER TABLE playlists ADD COLUMN rules TEXT;   -- JSON, NULL for ordinary playlists
```

`playlist_tracks` keeps holding the materialized rows for smart playlists
too. That is what lets every reader stay unchanged, and it is what the
server serves without needing to evaluate anything per request.

JSON, not a normalized `smart_conditions` table: the rules blob is only ever
read and written whole, never queried across, and it has to travel over the
sync wire as a unit anyway. A `SmartPlaylistRulesJsonContext` (source-
generated, matching `PlaylistSyncJsonContext`) keeps it AOT-safe.

## Sync

`PlaylistSyncPlaylistDto` gains a nullable `Rules`. Each device evaluates
against its own library — which is the desired behaviour, not a compromise:
on a phone holding a subset, "Recently Added" should mean recently added
*there*.

Merging two smart playlists is replacing the query with the more recent one.
No track-level diff, no conflict window: the existing `UpdatedAt`-vs-
baseline comparison in `PlaylistSyncPlanner` already expresses it, and since
materialization does not bump `UpdatedAt`, the only thing that can differ is
a real rule edit. `PlaylistSyncDecisionKind.Conflict` should therefore never
be reachable for a playlist that is smart on both sides — worth asserting in
a test.

Two edges, both cheap:

- **Smart on one side, manual on the other** (same `Id` — someone converted
  it). Newest `UpdatedAt` wins outright, including the change of kind. A
  manual playlist that wins brings its track list with it.
- **A membership rule referencing a playlist the peer lacks** — resolves to
  empty, per above.

## Server / Subsonic surface

Third-party clients see an ordinary playlist, because that is the only thing
OpenSubsonic can describe: `getPlaylists`/`getPlaylist` read the
materialized `playlist_tracks` rows and need no changes at all.

`updatePlaylist` against a smart playlist must be rejected rather than
silently accepted — an accepted edit would be erased by the next
recomputation, which is worse than an error. `createPlaylist` can only ever
make ordinary playlists; there is no wire vocabulary for rules, and adding
one to a published protocol needs a better reason than this
(CLAUDE.md, "No Users Yet" — third-party client compatibility is the one
place backward compatibility still binds).

Flower's own browser UI (`Flower.Web`) can have the real editor later; it is
not part of the first cut.

## UI

A rule editor in the shape Track Info just took (an editor with typed
fields, commit 2a77501): a rows-of-conditions list, each row Field /
Operator / Value with the value control chosen by the field's type, an
All/Any header, a limit row, and a live-updating checkbox.

Sidebar and list behaviour is the part that is easy to forget: drag-drop onto
a smart playlist, reorder within one, and removing a track from one would all
be silently undone by the next recompute. The refusals live in
`PlaylistManagementViewModel.AddTracks`/`ReorderTrack`, not in the view, because
the sidebar drop, the Add To Playlist menu and the mobile view all arrive
there — the UI hiding the affordance is then a courtesy rather than the only
thing standing between a user and a confusing no-op. (There is no "Remove from
playlist" command in the app at all yet, so there was nothing to hide; when one
is added it belongs behind the same guard.) A distinct sidebar icon
(`PlaylistStar`, via `SidebarItem.IconFor`) so the difference is visible before
the user tries.

"Convert to ordinary playlist" (freeze the current contents, drop the rules)
is a one-liner and worth having; the reverse is not offered.

## Phases

1. **Engine.** ✅ Done. `Flower.Core/Models/SmartPlaylistRules.cs` (rules,
   fields, operators, values, limits), `Flower.Core/Services/`
   `SmartPlaylistFields.cs` (the registry), `SmartPlaylistEvaluator.cs`
   (matching, limits, `Validate`, `EvaluateAll`) and `SmartPlaylistGraph.cs`
   (dependency order, cycle refusal, the editor's candidate list). 60 tests
   across `SmartPlaylistFieldsTests`, `SmartPlaylistEvaluatorTests` and
   `SmartPlaylistGraphTests` - pure, no VLC, ~40ms.

   One design point only the tests found: a condition's value shape has to
   be checked *before* the missing-value shortcut. "Year is <the text
   1979>" was reporting a clean miss on every track with no year tag, so an
   unevaluable rule looked exactly like one that did not match, and
   `Validate` - which runs conditions against a blank track - saw nothing
   wrong with it. `EnsureValueFits` now runs first, before anything reads
   the track.
2. **Persistence.** ✅ Done. `Schema.V6` (`ALTER TABLE playlists ADD COLUMN
   rules TEXT`) appended as its own migration step, `Playlist.Rules` /
   `IsSmart` / `Materialize`, `PlaylistRepository` reading and writing the
   blob, and `SmartPlaylistRulesJson` (+ its source-generated context) for the
   blob itself. Tests: round trip and migration in `StoreRoundTripTests`, the
   serialization shapes in `SmartPlaylistRulesJsonTests`, and the
   `UpdatedAt` invariant in `PlaylistTests`.

   Two decisions the writing settled. `SmartValue`'s JSON discriminators are
   short strings (`"text"`, `"relative"`, …) declared with `[JsonDerivedType]`
   rather than System.Text.Json's default, which is the assembly-qualified name
   of a private nested type - that would bake a namespace into every stored
   rule and make renaming the file a data migration. And `Read` returns null
   for anything it cannot parse instead of throwing: a blob can arrive from a
   peer or a newer build, and degrading one playlist to an ordinary one holding
   its last materialized contents beats failing the whole playlist load, which
   is the same tolerance `playlist_tracks` already applies to a track id that
   no longer resolves.
3. **Recomputation wiring.** ✅ Done.
   `Flower.Core/Services/SmartPlaylistRefresher.cs`: one debounced pass over
   every live-updating smart playlist, in `SmartPlaylistGraph` order, installed
   through `Playlist.Materialize`. `Start` subscribes to the five triggers
   above and runs an opening pass; `Refresh` is the pass itself; `RefreshOne`
   is what the editor will call on save, so a `LiveUpdating = false` playlist
   is still filled in once at the moment it is defined. Registered and started
   in both hosts — `App.axaml.cs` right after `ResetPlaylists`, `Program.cs`
   after the server's first rescan. 15 tests in
   `Flower.Tests/SmartPlaylistRefresherTests.cs`.

   Three things the writing settled. **A recomputation writes through a new
   `Library.SavePlaylists` rather than `PlaylistsChanged`** — not only because
   a recompute is not a sync-visible change, but because the refresher
   subscribes to that event (a membership rule makes one playlist another's
   input), so announcing its own write would feed straight back into itself.
   **`Library.TrackStarsChanged` is a new event**, for the reason in the
   trigger list above. And **`EvaluateAll` now seeds a `Random` per playlist
   from its id** instead of sharing one for the pass: a
   `LimitSelector.Random` playlist is re-evaluated on every play, since play
   counts are an input, and with an ambient `Random` it would draw a different
   25 songs each time — reshuffling itself under a listener partway through it.
   Seeded from the id, the same candidate set always yields the same pick, so
   the contents only move when the library does.
4. **UI.** ✅ Done. `SmartPlaylistEditorViewModel` +
   `SmartConditionRowViewModel` (rows, the All/Any header, the limit line, live
   updating, save/cancel), `Views/SmartPlaylistEditorWindow.axaml(.cs)` as a
   modal dialog over them, `SmartPlaylistLabels` in `Flower.Core` naming the
   operators and units, `SidebarItem.IconFor`, the two refusals in
   `PlaylistManagementViewModel`, and `CreateSmart`/`ConvertToOrdinary`.
   Reachable from the sidebar's context menu (Edit Rules… / Convert to Ordinary
   Playlist on a smart row, New Smart Playlist… on the header or empty space)
   and the Playlist app menu. 36 tests across
   `SmartPlaylistEditorViewModelTests` and `SmartPlaylistManagementTests`.

   Four things the writing settled. **The editor knows nothing about windows**,
   including that cancelling a playlist created solely to be edited deletes it
   again — which is what let all of it be tested without `Avalonia.Headless`.
   **A new smart playlist starts ordinary** and becomes smart only when the
   editor saves rules onto it, so the editor is an editor over an existing
   playlist rather than a two-mode create-or-edit thing. **Save calls
   `RefreshOne` as well as `Schedule`**, because a `LiveUpdating = false`
   playlist is left out of the recurring pass entirely and would otherwise sit
   empty forever. And **`MainViewModel` subscribes to
   `SmartPlaylistRefresher.Refreshed`** — a recompute deliberately does not
   raise `PlaylistsChanged`, so that event is the only signal that the list
   currently on screen is out of date.

   One thing the compiler settled: the option lists a `ComboBox` binds
   (`Fields`, `Operators`, the unit and selector tables) are exposed as instance
   properties, because a compiled binding resolves its path against
   `x:DataType`'s instance members and cannot reach a static one.
5. **Sync + server.** DTO field, planner assertions, `updatePlaylist`
   rejection.

Phase 5 is what is left: rules do not yet cross the wire, so a smart playlist
syncs as whatever tracks it currently holds and arrives at the peer as an
ordinary one.

## Testing

- Evaluator: one test per operator per value type, and the empty-library and
  no-conditions edges.
- Relative dates against a fixed fake clock, including the "same rules, a
  month later, different result" case that absolute storage would fail.
- Cycle rejection: the editor's candidate list excludes a transitive
  dependent; the evaluator throws rather than loops on a hand-built cycle.
- Materialization does not bump `UpdatedAt` (the sync-visibility invariant).
- `PlaylistSyncPlanner` never returns `Conflict` for smart-on-both-sides.
- Store round-trip: rules survive save/load, and a V5 database migrates to
  V6 without losing playlists.

Tests touching the stores must pin `PlatformDataDirectory.Current` to a
scratch directory, as the existing store tests do.

## Adjacent, deliberately separate

Seed-based "radio" — the other thing "intelligent playlist" can mean — is a
different feature: build a similarity score from what the library already
knows (genre, year, BPM, initial key, artist co-occurrence across playlists,
play history), seed it from a track, and **append to the queue** rather than
create a playlist. It shares no code with the rule engine, and entangling
them would give both a worse design. If it happens, it gets its own doc.
