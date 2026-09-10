#!/usr/bin/env bash
# Points Flower at a FFAudio.NET package built by CI, instead of at the copy of
# the façade that lives in Flower/Audio/Ffmpeg/ - and points it back.
#
# This exists because Phase 2 of docs/DECODER-LIBRARY-PLAN.md cannot be done in
# one step. Flower cannot delete its own FfmpegDecoder until the package is
# known to work in its place, and that cannot be known until Flower has been
# built and run against it. So this is the loop that closes that gap: a
# reversible switch, driven from CI's own artifact, that changes no source file
# at all.
#
#     scripts/use-ffaudio-package.sh              # latest green CI build of master
#     scripts/use-ffaudio-package.sh --run 1234   # one specific run
#     scripts/use-ffaudio-package.sh --status     # what is in force right now
#     scripts/use-ffaudio-package.sh --off        # back to the in-tree façade
#
# What makes it source-free is that the two are the same code under two sets of
# names - FFAudio.Decoder is Flower.Audio.Ffmpeg.FfmpegDecoder renamed, and
# nothing else. So the switch is four MSBuild `Using` aliases, which the SDK
# emits as global usings, plus a DefaultItemExcludes that drops the in-tree
# pair out of the compilation. Every call site keeps compiling, unedited, and
# `git status` stays clean: everything this writes goes into FFAudioPackage.props,
# which is gitignored and which Directory.Build.props imports only if it exists.
#
# The native library is deliberately NOT taken from the package. FFAudio.NET
# ships the managed binding alone today (its per-platform native packages are
# its own Phase 6), so what loads is still the ffaudio that
# native/ffmpeg/build-all.sh built in this repo - copied beside each test and
# app binary, which is the first place FFAudio's resolver looks. That is the
# right thing to be testing anyway: the question here is whether the package's
# managed layer works, not whose copy of one C file is on disk.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
props="$repo_root/FFAudioPackage.props"
feed="${FFAUDIO_FEED:-$HOME/.nuget/local-feeds/ffaudio}"
source_repo="${FFAUDIO_REPO:-yanos/FFAudio.NET}"

mode=on
run_id=""
while [ $# -gt 0 ]; do
    case "$1" in
        --off)    mode=off ;;
        --status) mode=status ;;
        --run)    run_id="${2:?--run needs a run id}"; shift ;;
        -h|--help) sed -n '2,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument '$1'. Try --help." >&2; exit 1 ;;
    esac
    shift
done

current_version() {
    [ -f "$props" ] || return 1
    sed -n 's:.*<FFAudioPackageVersion>\(.*\)</FFAudioPackageVersion>.*:\1:p' "$props" | head -1
}

if [ "$mode" = status ]; then
    if version="$(current_version)"; then
        echo "FFAudio.NET $version is in force (from $props)."
        echo "Turn it off with: scripts/use-ffaudio-package.sh --off"
    else
        echo "Flower is building its own Flower/Audio/Ffmpeg/ façade."
    fi
    exit 0
fi

if [ "$mode" = off ]; then
    if version="$(current_version)"; then
        rm -f "$props"
        echo "Removed $props - Flower is back on its in-tree façade (was FFAudio.NET $version)."
        echo "Rebuild to pick it up."
    else
        echo "Already on the in-tree façade; nothing to undo."
    fi
    exit 0
fi

command -v gh >/dev/null || { echo "This needs the gh CLI." >&2; exit 1; }

# The default branch rather than a hardcoded name: this repo says master and
# the habit of typing main is exactly how you end up testing nothing.
if [ -z "$run_id" ]; then
    branch="$(gh api "repos/$source_repo" --jq .default_branch)"
    echo "Looking for the newest green CI run on $source_repo@$branch ..."
    run_id="$(gh run list --repo "$source_repo" --branch "$branch" \
                --workflow CI --status success --limit 1 --json databaseId --jq '.[0].databaseId')"
    [ -n "$run_id" ] && [ "$run_id" != null ] \
        || { echo "No successful CI run on $branch to take a package from." >&2; exit 1; }
fi

gh run view --repo "$source_repo" "$run_id" \
    --json displayTitle,headSha,createdAt \
    --jq '"run \(env.RUN_ID // "")  \(.displayTitle)  \(.headSha[0:8])  \(.createdAt)"' RUN_ID="$run_id" 2>/dev/null \
  || gh run view --repo "$source_repo" "$run_id" --json displayTitle,headSha --jq '"\(.displayTitle)  \(.headSha[0:8])"'

staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT
gh run download "$run_id" --repo "$source_repo" --name nupkg --dir "$staging"

package="$(find "$staging" -name 'FFAudio.NET.*.nupkg' -not -name '*.snupkg' | head -1)"
[ -n "$package" ] || { echo "That run's nupkg artifact holds no FFAudio.NET package." >&2; exit 1; }

file="$(basename "$package")"
version="${file#FFAudio.NET.}"
version="${version%.nupkg}"

mkdir -p "$feed"
cp "$staging"/*.nupkg "$staging"/*.snupkg "$feed/" 2>/dev/null || cp "$staging"/*.nupkg "$feed/"

# NuGet extracts a package into the global cache once and never looks at the
# .nupkg again, so a rebuilt package at a version already extracted would
# restore as whatever was there before. Re-running the same CI commit is the
# ordinary way to hit that.
extracted="${NUGET_PACKAGES:-$HOME/.nuget/packages}/ffaudio.net/$version"
if [ -d "$extracted" ]; then
    echo "Evicting the already-extracted $version from the global cache."
    rm -rf "$extracted"
fi

# The façade this repo built, named for this host. Copied beside each binary
# because FFAudio's resolver looks at AppContext.BaseDirectory first, and its
# repo-relative fallback walks native/artifacts/ - FFAudio.NET's own layout,
# not Flower's native/ffmpeg/artifacts/.
case "$(uname -s)" in
    Darwin) native="native/ffmpeg/artifacts/macos/libffaudio.dylib" ;;
    Linux)  native="native/ffmpeg/artifacts/linux/libffaudio.so" ;;
    *)      native="native/ffmpeg/artifacts/windows/ffaudio.dll" ;;
esac
[ -f "$repo_root/$native" ] \
    || echo "Note: $native is not built yet - run native/ffmpeg/build-all.sh, or nothing will decode."

cat > "$props" <<PROPS
<Project>
  <!-- Generated by scripts/use-ffaudio-package.sh. Gitignored, and imported by
       Directory.Build.props only when it exists, so deleting it is the whole
       of the undo. Do not edit by hand; re-run the script instead. -->
  <PropertyGroup>
    <FFAudioPackageVersion>$version</FFAudioPackageVersion>
    <!-- A folder is a valid NuGet source, and naming it here rather than in a
         NuGet.config keeps it out of everyone else's restore. -->
    <RestoreAdditionalProjectSources>\$(RestoreAdditionalProjectSources);$feed</RestoreAdditionalProjectSources>
  </PropertyGroup>

  <!-- The three projects whose own sources name the façade's types. Everything
       else reaches the decoder through ITrackDecoder and never says FFmpeg. -->
  <ItemGroup Condition="'\$(MSBuildProjectName)' == 'Flower' Or '\$(MSBuildProjectName)' == 'Flower.Tests' Or '\$(MSBuildProjectName)' == 'Flower.DeviceChecks'">
    <PackageReference Include="FFAudio.NET" Version="$version" />
    <!-- The renames, and nothing else - see the script's header. The SDK turns
         these into global usings, so every existing call site compiles as it
         stands and the switch leaves no diff behind. -->
    <Using Include="FFAudio.Decoder" Alias="FfmpegDecoder" />
    <Using Include="FFAudio.SampleFormat" Alias="FfmpegSampleFormat" />
    <Using Include="FFAudio.AudioFormat" Alias="FfmpegAudioFormat" />
    <Using Include="FFAudio.DecodeException" Alias="FfmpegDecodeException" />
  </ItemGroup>

  <!-- DefaultItemExcludes rather than <Compile Remove>: Directory.Build.props is
       imported before the SDK globs the source tree, so a Remove here would run
       against an empty list and quietly do nothing. FfmpegTrackDecoder stays -
       it is Flower's ITrackDecoder adapter, not part of the façade. -->
  <PropertyGroup Condition="'\$(MSBuildProjectName)' == 'Flower'">
    <DefaultItemExcludes>\$(DefaultItemExcludes);Audio/Ffmpeg/FfmpegDecoder.cs;Audio/Ffmpeg/FfmpegNative.cs</DefaultItemExcludes>
  </PropertyGroup>

  <!-- Flower.MacOS already copies this for itself, and its TFM is net10.0-macos,
       so the condition leaves it alone rather than fighting it over one file. -->
  <ItemGroup Condition="'\$(TargetFramework)' == 'net10.0' And Exists('\$(MSBuildThisFileDirectory)$native')">
    <None Include="\$(MSBuildThisFileDirectory)$native">
      <Link>$(basename "$native")</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
PROPS

echo
echo "Flower is now building against FFAudio.NET $version"
echo "  package : $feed/$file"
echo "  switch  : $props  (gitignored; delete it or run --off to undo)"
echo
echo "Try it:"
echo "  dotnet test Flower.Tests/Flower.Tests.csproj --filter Category=RequiresFfmpeg"
echo "  dotnet run --project Flower.MacOS/Flower.MacOS.csproj"
