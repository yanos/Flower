#!/usr/bin/env python3
"""Which moving image tags a release tag takes: `latest` and `beta`.

Usage: git tag -l 'v*' | scripts/image-channels.py v1.2.0-beta.1

Prints `latest=true|false` and `beta=true|false`, for $GITHUB_OUTPUT (the
image job in .github/workflows/tests.yml). The same rule as the desktops'
UpdateChannel, so a server on `beta` runs what a desktop with betas on is
offered:

- `latest` is the newest release (no pre-release label).
- `beta` is the newest release or beta (`-beta`, `-beta.N`), whichever is
  higher. Any other label (`-alpha`, `-rc`, a CI build's) takes neither.

Each goes to this tag only if no tag of the same channel outranks it, so a
fix to an older line, tagged after a newer one, never moves either back.
"""

import re
import sys

VERSION = re.compile(r"[vV]?([0-9]+(?:\.[0-9]+){0,2})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?")


def parse(tag):
    match = VERSION.fullmatch(tag.strip())
    if not match:
        return None
    text, pre = match.groups()
    core = tuple(int(part) for part in text.split("."))
    if any(part > 2147483647 for part in core):
        return None
    return core + (0,) * (3 - len(core)), pre


def channel(pre):
    if pre is None:
        return "stable"
    if pre.split(".")[0].lower() == "beta":
        return "beta"
    return None


# Semantic-version precedence (semver.org, section 11), as ReleaseVersion.
def key(version):
    core, pre = version
    if pre is None:
        return core, (1,)
    ids = tuple((0, int(i), "") if i.isdigit() else (1, 0, i) for i in pre.split("."))
    return core, (0,) + ids


def main():
    tag = sys.argv[1]
    this = parse(tag)
    this_channel = channel(this[1]) if this else None

    versions = [v for v in (parse(t) for t in sys.stdin) if v]
    stable = [v for v in versions if channel(v[1]) == "stable"]
    beta = [v for v in versions if channel(v[1]) in ("stable", "beta")]

    def newest_of(pool):
        return this is not None and all(key(v) <= key(this) for v in pool)

    print(f"latest={'true' if this_channel == 'stable' and newest_of(stable) else 'false'}")
    print(f"beta={'true' if this_channel in ('stable', 'beta') and newest_of(beta) else 'false'}")


if __name__ == "__main__":
    main()
