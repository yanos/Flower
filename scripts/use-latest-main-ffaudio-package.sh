#!/usr/bin/env bash
# Builds Flower against an unpublished FFAudio.NET - a CI build or a local
# pack of ../FFAudio.NET - instead of the version pinned in
# Directory.Build.props, and back again. For proving a library change against
# Flower before it is released.
#
# It writes FFAudioPackage.local.props (gitignored), which Directory.Build.props
# imports: a version and a folder feed holding the packages. Deleting that file
# is the whole of the undo.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: scripts/use-ffaudio-package.sh [option]

Build Flower against an unpublished FFAudio.NET instead of the pinned one.

  (none)      newest green CI build of FFAudio.NET's default branch
  --run ID    a specific CI run
  --local     pack ../FFAudio.NET as it stands (override: FFAUDIO_SOURCE_DIR)
  --status    show which version is in force
  --off       back to the pinned version

Rebuild after switching.
EOF
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
props="$repo_root/FFAudioPackage.local.props"
feed="${FFAUDIO_FEED:-$HOME/.nuget/local-feeds/ffaudio}"
source_repo="${FFAUDIO_REPO:-yanos/FFAudio.NET}"
source_dir="${FFAUDIO_SOURCE_DIR:-$(cd "$repo_root/.." && pwd)/FFAudio.NET}"

pinned="$(sed -n 's:.*<FFAudioPackageVersion[^>]*>\(.*\)</FFAudioPackageVersion>.*:\1:p' "$repo_root/Directory.Build.props" | head -1)"

# The desktop payload Flower.csproj picks for this host, plus the phone
# payloads this host can build heads for. A local pack skips a phone payload
# whose native was never cross-compiled next door; that head then fails to
# restore at the override version, which is the honest outcome.
case "$(uname -s)" in
    Darwin) payloads="FFAudio.NET.macOS FFAudio.NET.iOS FFAudio.NET.Android" ;;
    Linux)  payloads="FFAudio.NET.Linux FFAudio.NET.Android" ;;
    *)      payloads="FFAudio.NET.Windows FFAudio.NET.Android" ;;
esac

mode=on
run_id=""
local_pack=0
while [ $# -gt 0 ]; do
    case "$1" in
        --off)     mode=off ;;
        --status)  mode=status ;;
        --run)     run_id="${2:?--run needs a run id}"; shift ;;
        --local)   local_pack=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument '$1'." >&2; usage >&2; exit 1 ;;
    esac
    shift
done

current_version() {
    [ -f "$props" ] || return 1
    sed -n 's:.*<FFAudioPackageVersion>\(.*\)</FFAudioPackageVersion>.*:\1:p' "$props" | head -1
}

# A façade staged into bin/ or obj/ by an earlier build is not overwritten by
# every later one - the macOS head re-bundles whatever sits in
# obj/<rid>/nativelibraries/ - so a switch in either direction would otherwise
# leave the head running the previous version's native.
sweep_stale_copies() {
    find "$repo_root" -type f \
        \( -name libffaudio.dylib -o -name libffaudio.so -o -name ffaudio.dll \) \
        \( -path '*/bin/*' -o -path '*/obj/*' \) -delete 2>/dev/null || true
    find "$repo_root" -type d -name ffaudio.framework \
        \( -path '*/bin/*' -o -path '*/obj/*' \) -exec rm -rf {} + 2>/dev/null || true
}

if [ "$mode" = status ]; then
    if version="$(current_version)"; then
        echo "FFAudio.NET $version (override in $props; pinned is $pinned)"
    else
        echo "FFAudio.NET $pinned (pinned)"
    fi
    exit 0
fi

if [ "$mode" = off ]; then
    sweep_stale_copies
    if version="$(current_version)"; then
        rm -f "$props"
        echo "Back on FFAudio.NET $pinned (was $version). Rebuild to pick it up."
    else
        echo "Already on the pinned FFAudio.NET $pinned."
    fi
    exit 0
fi

staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

if [ "$local_pack" -eq 1 ]; then
    [ -d "$source_dir" ] || { echo "No FFAudio.NET checkout at $source_dir - set FFAUDIO_SOURCE_DIR." >&2; exit 1; }
    echo "Packing $source_dir as it stands ..."
    dotnet pack "$source_dir/src/FFAudio.NET/FFAudio.NET.csproj" \
        --configuration Release --output "$staging" --nologo -v quiet
    for payload in $payloads; do
        # A payload project refuses to pack without its native (its
        # EnsurePayloadExists). The desktop one is required; a phone one that
        # was never cross-compiled is skipped rather than fatal.
        if ! dotnet pack "$source_dir/packaging/$payload/$payload.csproj" \
                --configuration Release --output "$staging" --nologo -v quiet >/dev/null 2>&1; then
            case "$payload" in
                *.iOS|*.Android) echo "Skipped $payload: no native built in $source_dir/native/artifacts." ;;
                *) echo "Could not pack $payload - build it first: $source_dir/native/build-all.sh" >&2; exit 1 ;;
            esac
        fi
    done
else
    command -v gh >/dev/null || { echo "This needs the gh CLI." >&2; exit 1; }
    if [ -z "$run_id" ]; then
        branch="$(gh api "repos/$source_repo" --jq .default_branch)"
        echo "Looking for the newest green CI run on $source_repo@$branch ..."
        run_id="$(gh run list --repo "$source_repo" --branch "$branch" \
                    --workflow CI --status success --limit 1 --json databaseId --jq '.[0].databaseId')"
        [ -n "$run_id" ] && [ "$run_id" != null ] \
            || { echo "No successful CI run on $branch to take a package from." >&2; exit 1; }

        # The newest green run is not necessarily the newest commit: when the
        # tip of the branch failed, or is still building, what gets taken is
        # an older package, and a change expected to be in it may not be.
        head_sha="$(gh api "repos/$source_repo/commits/$branch" --jq .sha)"
        green_sha="$(gh run view --repo "$source_repo" "$run_id" --json headSha --jq .headSha)"
        if [ "$head_sha" != "$green_sha" ]; then
            head_run="$(gh run list --repo "$source_repo" --branch "$branch" --workflow CI \
                          --commit "$head_sha" --limit 1 \
                          --json status,conclusion,url \
                          --jq '.[0] | if . == null then "" else "\(if .conclusion == "" then .status else .conclusion end) (\(.url))" end')"
            echo >&2
            echo "WARNING: $branch is not green - CI on its tip, ${head_sha:0:8}: ${head_run:-not run}." >&2
            echo "         Taking the newest green build instead, ${green_sha:0:8}, which is behind it." >&2
            echo >&2
        fi
    fi
    gh run view --repo "$source_repo" "$run_id" --json displayTitle,headSha \
        --jq '"\(.displayTitle)  \(.headSha[0:8])"'
    gh run download "$run_id" --repo "$source_repo" --name nupkg --dir "$staging"
fi

# The binding specifically: its payload siblings match a plain FFAudio.NET.*
# glob too, and would give the wrong version out of their file names.
package="$(find "$staging" -name 'FFAudio.NET.[0-9]*.nupkg' | head -1)"
[ -n "$package" ] || { echo "No FFAudio.NET package was produced." >&2; exit 1; }
version="$(basename "$package")"
version="${version#FFAudio.NET.}"
version="${version%.nupkg}"

mkdir -p "$feed"
cp "$staging"/*.nupkg "$feed/"

# NuGet extracts a version into the global cache once and never re-reads the
# .nupkg, so a rebuilt package at an already-extracted version would restore
# as the old one. Re-running the same CI commit is the ordinary way to hit it.
cache="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
for id in ffaudio.net $(echo "$payloads" | tr '[:upper:]' '[:lower:]'); do
    rm -rf "${cache:?}/$id/$version"
done

sweep_stale_copies

cat > "$props" <<PROPS
<Project>
  <!-- Written by scripts/use-ffaudio-package.sh and gitignored. Delete it, or
       run the script with --off, to go back to the pinned version. -->
  <PropertyGroup>
    <FFAudioPackageVersion>$version</FFAudioPackageVersion>
    <RestoreAdditionalProjectSources>\$(RestoreAdditionalProjectSources);$feed</RestoreAdditionalProjectSources>
  </PropertyGroup>
</Project>
PROPS

echo "Flower now builds against FFAudio.NET $version (from $feed). Rebuild to pick it up."
