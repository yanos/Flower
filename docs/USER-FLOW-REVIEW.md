# User-flow review

Bugs a person using Flower would hit: data lost, the wrong thing done, a dead
end, or behaviour that misleads. The `user-flow-review` skill finds them and
keeps this list; `/release` treats every open **P0** here as blocking the tag.

**Status:** 4 open (0 P0, 0 fix-now, 4 later), UF-12 to UF-15, all from the review of
`ef6695fb` on 2026-11-22.

Severities are the ones in `.claude/skills/release/REVIEW.md`. **Reproduced**
means a throwaway test against an export of that commit went red on the bug, or,
from UF-19 on, a throwaway harness did, driving the real sync services against a
real server. **Read** means the finding comes from tracing the code. No finding
was checked through the real UI, or on Windows, Linux or a phone. Line numbers
are as of the commit named for that finding.

An id is never reused. A fixed finding leaves this list, and its commit records
the fix.

## P0: data lost

None open.

## Later: confusing, or impossible to do

**UF-12. Dragging a removed song back in silently does nothing.** *Read.*
The only way back is Settings' Removed Songs, and nothing says so.
`Flower.Core/Services/LibraryFolderWatcher.cs:351`.
*Direction:* an explicit drop restores the song.
