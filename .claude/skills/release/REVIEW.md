# Release review briefs

Three briefs, one subagent each, dispatched in parallel by step 2 of
[SKILL.md](SKILL.md). Give each one `PREV` (the last tag) and the brief
verbatim. Each reports findings as: severity (**P0** / **fix-now** / **later**),
`file:line`, the defect, and a concrete failure scenario (inputs and state →
wrong outcome). A finding without a failure scenario is a question, labelled
as one. Subagents read and report; they do not edit.

**P0** means: a user's files or library are lost or damaged, a listener
reaches what they should not, the app or server cannot start or play, or
installs can no longer update (so the fix could never ship).

## Docs

Every user-facing and agent-facing document matches the code at `HEAD`.
Read each one in full against the code it describes; `git diff $PREV..HEAD`
says where drift is likeliest, but stale text from before `PREV` counts too.

- `README.md`: install steps, server commands, the docs table, every link.
- `LICENSE`, `NOTICE`, `licenses/`: every third-party component a build ships
  (every csproj's `PackageReference`s, `native/`,
  the CLAP model, fonts, icons) is in `NOTICE` with its licence; nothing
  listed is gone.
- `docs/GETTING-STARTED.md`: every UI label quoted matches a view's text.
- `docs/SELF-HOSTING.md`: config keys and defaults against the server's
  options classes, compose snippets against `docker/`.
- `docs/API.md`: every route in `Flower.Server/Endpoints/` is documented,
  and nothing documented is gone.
- `docs/PRIVACY.md`: against `CrashReporting`, `CrashScrubber` and every
  outgoing request (online metadata, update checks, public-address probe).
- `docs/SECURITY.md`, `docs/REMOTE-ACCESS.md`, `docs/MANAGING-SONGS.md`.
- Every `docs/*-PLAN.md`: the status and "what is left" sections against
  what `$PREV..HEAD` built; `docs/todo.txt`.
- `CLAUDE.md`, `AGENTS.md` (and drift between the two), `Tests/README.md`
  (job names and counts against `.github/workflows/tests.yml`).

Done when every file above has been read and checked; report per file, "no
drift" included.

## Security

The trust boundary is load-bearing (CLAUDE.md, "How It Gets Used"): listeners
connect from outside the house. Review `git diff $PREV..HEAD`, following each
change into the code it calls and the code that calls it.

- Auth: every route under `/api/flower/v1/*` still requires a device
  signature or stream ticket; `PeerSignatureAuth`, `LanGuard`, pairing-code
  redemption, admin-only routes, replay and expiry.
- Isolation: one listener reaching another's playlists, history or devices;
  a listener device reaching ingest or admin routes.
- Input: path traversal in any path or file name from a request or a synced
  payload, unbounded sizes, per-device budgets and rate limits.
- Leaks: secrets, tokens, pairing codes or personal paths in logs, crash
  reports, `/info`, error bodies (`FlowerProblem`); secrets committed in the
  diff.
- `docs/SECURITY.md` invariants still hold.

Done when every changed file touching the server, sync, network or
persistence has been read.

## P0

Hunt the failures a release cannot survive. Start from `git diff $PREV..HEAD`,
then follow each code path below even where the diff does not touch it, since
a change elsewhere can feed it new input.

- **Wrong file deletion**: every `File.Delete`, `Directory.Delete`, move,
  trash and overwrite. Can it act outside `DeviceLibraryFolders.ScanPaths` or
  Flower's own folder; on a path built from server or sync data; across a
  symlink, case-insensitive match or unnormalized path? Ingest: a deletion is
  a removal, never a delete (`docs/SYNC-PLAN.md`, "Ingest").
- **Library corruption**: `flower.db` schema or write changes since `PREV`,
  and what they do to an existing database (lambda's, a phone's) on first
  open; `settings.json` and `queue.json` writes surviving a crash
  mid-write; `Library.UpdateTracks` losing identity, `DateAdded` or play
  history; a rescan of an unmounted or empty folder (the NAS over NFS) read as
  "every song removed"; sync merges overwriting newer data; TagLib# writes
  damaging audio files.
- **Can't start, can't play**: startup on each head, first launch with no
  data, the decoder and `libflowerbridge` load, an iOS NativeAOT trimming or
  reflection change.
- **Can't update**: `GitHubReleaseUpdater` (macOS) and `VelopackUpdater`
  (Windows, Linux) still find and apply the next release; installer packaging
  in `scripts/package-desktop.sh`.
- **Test isolation**: a new test that writes app data without
  `PinnedDataDirectory` and `[Collection("PlatformDataDirectory")]`.

Done when every item above has been traced through the code at `HEAD`.
