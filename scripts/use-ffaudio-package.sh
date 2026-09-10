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
#     scripts/use-ffaudio-package.sh --local      # pack ../FFAudio.NET as it stands
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
# The native library does not come from the package either, because FFAudio.NET
# ships the managed binding alone until its own Phase 6. It comes from the
# FFAudio.NET checkout next door, copied beside each test and app binary, which
# is the first place FFAudio's resolver looks.
#
# Its own and not Flower's, which is a correction rather than a preference. The
# two façades were the same C file when this script was written, so either
# would do; Phase 3 added tags, cover art, channel layout and codec names to
# one of them, and the managed side now calls two of those from the Decoder
# constructor. Against Flower's older library every open would fail with
# EntryPointNotFoundException - not an ABI mismatch, because nothing changed
# shape and the version is still 1, but a missing symbol. Pairing the package
# with the tree it was built from is the only arrangement that stays honest as
# the library moves ahead of what Flower vendors.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
props="$repo_root/FFAudioPackage.props"
feed="${FFAUDIO_FEED:-$HOME/.nuget/local-feeds/ffaudio}"
source_repo="${FFAUDIO_REPO:-yanos/FFAudio.NET}"
# The checkout next door, which is where the façade to pair with the package
# comes from. Overridable for a clone that lives somewhere else.
source_dir="${FFAUDIO_SOURCE_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/FFAudio.NET}"

mode=on
run_id=""
local_pack=0
while [ $# -gt 0 ]; do
    case "$1" in
        --off)    mode=off ;;
        --status) mode=status ;;
        --run)    run_id="${2:?--run needs a run id}"; shift ;;
        --local)  local_pack=1 ;;
        -h|--help) sed -n '2,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument '$1'. Try --help." >&2; exit 1 ;;
    esac
    shift
done


# Copies made by a previous toggle, removed on every switch in either
# direction. CopyToOutputDirectory is PreserveNewest, and the façade this
# script installs is by definition newer than Flower's vendored one - so
# switching off and rebuilding would leave the package's library sitting in an
# already-built output, and the head would keep running against it while every
# file on disk said otherwise. Only build output is touched; the checked-in
# mobile binaries under Flower.iOS/Frameworks and Flower.Android/libs are not
# under bin/ or obj/ and are left alone.
#
# obj/ as well as bin/, which is not belt and braces: the macOS head stages
# native libraries through obj/<rid>/nativelibraries/ and an incremental build
# re-bundles whatever is sitting there, so sweeping only bin/ left the head
# running the package's façade while every file on disk said it was switched
# off.
sweep_stale_copies() {
    find "$repo_root" \
        -type f \
        \( -name libffaudio.dylib -o -name libffaudio.so -o -name ffaudio.dll \) \
        \( -path '*/bin/*' -o -path '*/obj/*' \) \
        -delete 2>/dev/null || true
}

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
    # Unconditionally, not only when a props file was there to remove: a
    # half-undone switch - props already gone, copies still staged - is
    # exactly the state that needs sweeping, and the state a plain `rm` of
    # the props file leaves behind.
    sweep_stale_copies
    if version="$(current_version)"; then
        rm -f "$props"
        echo "Removed $props - Flower is back on its in-tree façade (was FFAudio.NET $version)."
        echo "Rebuild to pick it up."
    else
        echo "Already on the in-tree façade; nothing to undo."
    fi
    exit 0
fi

[ -d "$source_dir" ] || { echo "No FFAudio.NET checkout at $source_dir - set FFAUDIO_SOURCE_DIR." >&2; exit 1; }

staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

if [ "$local_pack" -eq 1 ]; then
    # The working tree as it stands, which is the loop you want while actually
    # writing the library: pack, switch, run Flower's suite, repeat, with no
    # commit and no CI round trip in between.
    echo "Packing $source_dir as it stands ..."
    dotnet pack "$source_dir/src/FFAudio.NET/FFAudio.NET.csproj" \
        --configuration Release --output "$staging" --nologo -v quiet
else
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

    gh run view --repo "$source_repo" "$run_id" --json displayTitle,headSha \
        --jq '"\(.displayTitle)  \(.headSha[0:8])"'
    gh run download "$run_id" --repo "$source_repo" --name nupkg --dir "$staging"
fi

package="$(find "$staging" -name 'FFAudio.NET.*.nupkg' -not -name '*.snupkg' | head -1)"
[ -n "$package" ] || { echo "No FFAudio.NET package was produced." >&2; exit 1; }

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

# The façade to pair the package with, named for this host. Copied into the
# feed rather than referenced where it lies, so that switching off and
# rebuilding FFAudio.NET cannot change what Flower is currently testing
# against.
case "$(uname -s)" in
    Darwin) platform=macos;   libname=libffaudio.dylib ;;
    Linux)  platform=linux;   libname=libffaudio.so ;;
    *)      platform=windows; libname=ffaudio.dll ;;
esac

built="$source_dir/native/artifacts/$platform/$libname"
if [ ! -f "$built" ]; then
    echo
    echo "$source_dir has no built façade at native/artifacts/$platform/." >&2
    echo "Build it there first - native/build-all.sh $platform - because the package's" >&2
    echo "managed side calls symbols Flower's own vendored façade does not have." >&2
    exit 1
fi

sweep_stale_copies
mkdir -p "$feed/native"
cp "$built" "$feed/native/$libname"
native="$feed/native/$libname"

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

  <!-- Named hosts rather than a TargetFramework condition, because a
       CopyToOutputDirectory item flows through a ProjectReference into
       everything downstream. On the Flower library that put the dylib into
       every consumer's output, including the macOS head - which copies one
       itself, under the same name, from a different path - and two sources for
       one bundle file is an install_name_tool failure with a missing .tmp
       rather than a duplicate-item error. It would also have pushed a desktop
       dylib into the iOS and Android app bundles, where it is at best dead
       weight.

       So only the three projects that are actually launched, and none of the
       libraries beneath them. -->
  <ItemGroup Condition="('\$(MSBuildProjectName)' == 'Flower.Tests' Or '\$(MSBuildProjectName)' == 'Flower.Desktop' Or '\$(MSBuildProjectName)' == 'Flower.MacOS') And Exists('$native')">
    <None Include="$native">
      <Link>$libname</Link>
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
# The phones load the façade out of Flower.iOS/Frameworks and
# Flower.Android/libs, which are checked-in binaries of Flower's own vendored
# source. Nothing here redirects those, so a mobile head built while this
# switch is on pairs new managed code with an older library.
echo "Note: this switch reaches the desktop heads and the test suite only."
echo "      Flower.iOS and Flower.Android still load their checked-in façade, which"
echo "      predates the package's metadata calls - run those switched off."
echo
echo "Try it:"
echo "  dotnet test Flower.Tests/Flower.Tests.csproj --filter Category=RequiresFfmpeg"
echo "  dotnet run --project Flower.MacOS/Flower.MacOS.csproj"
