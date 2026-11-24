---
name: user-flow-review
description: User-flow bug review of Flower, walking what listeners and owners actually do to find data loss, wrong actions, dead ends and misleading behaviour. Use when asked to hunt user-facing or unexpected-behaviour bugs, and from /release.
argument-hint: "[since-ref] [--report-only]"
context: fork
---

# User-flow review

Arguments: `$ARGUMENTS`

Review Flower the way it gets used. A diff review asks whether code is correct;
this asks what happens to a person, their files and their playlists as they move
through the app, from the golden path to the convoluted one. Every finding is a
**failure scenario**: a setup, the steps taken, and the wrong outcome they see.

- A git ref scopes the run to what changed since it (step 1).
- `--report-only` leaves the working tree untouched: `/release` records the
  findings itself once they are triaged.

## 1. Scope

Read [docs/USER-FLOW-REVIEW.md](../../../docs/USER-FLOW-REVIEW.md), the ledger of
open findings, and re-check each against `HEAD`: still open, or fixed. No file
means none are open: it is deleted when its last finding is fixed.

Then pick the flows from [FLOWS.md](FLOWS.md):

- **No ref:** every flow.
- **A ref:** every data flow, plus each everyday flow that `git diff <ref>..HEAD`
  touches or feeds.

Done when each ledger finding is marked open or fixed and you hold the list of
flows to walk.

## 2. Walk

For each flow, under each setup it applies to, trace the action from where the
person starts it (the view, menu or shortcut) through the view model and service
to what lands on disk or the wire, then back to what the screen says. Follow the
state into the next launch and onto the next device. Put every lens in FLOWS.md
to each step.

Then trace it sideways. Name the state the action changes and search the code for
its **dependents**: everything that reads it, was built from it or holds a copy,
whichever flow they belong to. Replay the action with each dependent live, and
follow it to the app's next move, which is often where the wrong outcome shows.

Done when every flow in scope has a line in your notes: its candidates, or
"clean" with the setups walked and the dependents checked, by name.

## 3. Reproduce

Reproduce every candidate that would be **P0** or **fix-now** (severities in
[release/REVIEW.md](../release/REVIEW.md)), in an isolated export:

```bash
scratch=$(mktemp -d) && git archive HEAD | tar -x -C "$scratch"
```

Add throwaway tests there that assert what the user would expect, so **red** is
the bug. Run them with `dotnet test Tests/Flower.Tests/Flower.Tests.csproj
-p:IncludeWebUi=false --filter <name>`. What the scenarios usually need:

- **Client and server end to end:** add to the export's
  `Tests/Flower.Tests/Sync/Scenarios/LibraryMirrorScenarioTests.cs`, whose
  `Device` and `SimulatedFlowerServer` run real scans, real tagged files and real
  sync.
- **Across launches:** a fresh `FlowerDb`, `Library` and
  `PlaylistRepository.Load` on the same database file for each launch.
- **A full or separate disk (macOS):** `hdiutil create -size 12m -fs HFS+`, attach
  it with `-nobrowse`, and detach it when finished.

A candidate that goes green is dropped. Done when each P0 and fix-now finding is
labelled **reproduced**, or **read** with the reason it could not be reproduced,
and every scratch volume is detached and every `Flower.Server` you started is
killed (CLAUDE.md, "Throwaway servers").

## 4. Report

Each finding carries: severity, `file:line`, the failure scenario, reproduced or
read, and the direction of a fix. Rank data loss first.

Unless `--report-only` was passed, update `docs/USER-FLOW-REVIEW.md`: add new
findings under the next unused `UF-` id, remove fixed ones, and refresh its status
line with the commit reviewed and the date. Delete the file when no finding is
left open. When there is no file and a finding stays open, create it with the
header its last version had (`git log --diff-filter=D -1 -- docs/USER-FLOW-REVIEW.md`
names the commit that deleted it; show its parent's copy). An id is never
reused: the next one follows the highest in that file's history (`git log -p --
docs/USER-FLOW-REVIEW.md`), UF-24 when it was deleted.

Return the ranked findings, the ledger findings now fixed, the flows walked, and
what was not checked (platforms, real UI). Done when every flow in scope is
accounted for in that report.
