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
# The native library comes from a package too, which it did not always. Until
# FFAudio.NET's Phase 6 the package was the managed binding alone, so this
# script copied a façade out of the checkout next door and dropped it beside
# each binary. There are now payload packages - FFAudio.NET.macOS, .Linux,
# .Windows - carrying runtimes/<rid>/native/, which the SDK resolves with no
# help from anyone, so the copy step is gone and this switch is a pair of
# PackageReferences and nothing else.
#
# On a Mac the switch reaches the iOS heads as well, through a third package.
# It has to be a different mechanism rather than a fourth RID folder: .NET for
# iOS does not resolve a native out of runtimes/, and a framework is a
# directory rather than a file, so FFAudio.NET.iOS ships a .targets file that
# declares the <NativeReference> in whatever project references it. Flower.iOS
# and Flower.DeviceChecks.iOS declare their own, out of Flower.iOS/Frameworks,
# so this switch suppresses those - two ffaudio.frameworks in one app is either
# a duplicate-symbol failure or, worse, the wrong one silently winning, and the
# checked-in one is the one that predates the metadata calls.
#
# That is worth more than the tidiness. The copy proved the managed side; it
# could not prove the packaging, which is the half nobody had exercised - and
# a payload that fails to resolve is exactly what a consumer would hit first.
# Now the same switch that tests the binding tests the package that ships it.
#
# It also has to be the package's own façade rather than Flower's, which is a
# correction rather than a preference. The two were the same C file when this
# script was written; FFAudio.NET's Phase 3 then added tags, cover art, channel
# layout and codec names to one of them, and the managed side calls two of
# those from the Decoder constructor. Against Flower's vendored library every
# open fails with EntryPointNotFoundException - not an ABI mismatch, since
# nothing changed shape and the version is still 1, but a missing symbol.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
props="$repo_root/FFAudioPackage.props"
feed="${FFAUDIO_FEED:-$HOME/.nuget/local-feeds/ffaudio}"
source_repo="${FFAUDIO_REPO:-yanos/FFAudio.NET}"
# The checkout next door, which --local packs from. Overridable for a clone
# that lives somewhere else; the default path needs no checkout at all.
source_dir="${FFAUDIO_SOURCE_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/FFAudio.NET}"

# One payload package per desktop, named for the host. There is no cross-
# platform one and there should not be: a Windows FFmpeg is seventy megabytes
# where a macOS dylib is a few, and an app has no use for the other two.
#
# iOS is a second payload rather than a different one, and only on a Mac -
# nowhere else can build an iOS head at all, so asking for the package there
# would be downloading a framework to ignore it.
case "$(uname -s)" in
    Darwin) platform=macos;   payload=FFAudio.NET.macOS;   ios_payload=FFAudio.NET.iOS ;;
    Linux)  platform=linux;   payload=FFAudio.NET.Linux;   ios_payload= ;;
    *)      platform=windows; payload=FFAudio.NET.Windows; ios_payload= ;;
esac

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


# Façades left in a build output by a previous toggle, removed on every switch
# in either direction. Nothing rebuilt after a switch-off would overwrite them:
# the package's payload is copied in by the SDK and Flower's own build stages
# its vendored one under a different path, so a stale file simply stays and the
# head keeps running against it while every file on disk says otherwise. Only
# build output is touched; the checked-in mobile binaries under
# Flower.iOS/Frameworks and Flower.Android/libs are not under bin/ or obj/ and
# are left alone.
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

    # Frameworks are directories, so the file sweep above does not see them,
    # and an iOS build embeds one into the .app rather than beside a binary.
    # Same failure either way: whichever ffaudio was staged last keeps running
    # while every file on disk says otherwise.
    find "$repo_root" \
        -type d -name ffaudio.framework \
        \( -path '*/bin/*' -o -path '*/obj/*' \) \
        -exec rm -rf {} + 2>/dev/null || true
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

if [ "$local_pack" -eq 1 ] && [ ! -d "$source_dir" ]; then
    echo "No FFAudio.NET checkout at $source_dir - set FFAUDIO_SOURCE_DIR." >&2
    exit 1
fi

staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

if [ "$local_pack" -eq 1 ]; then
    # The working tree as it stands, which is the loop you want while actually
    # writing the library: pack, switch, run Flower's suite, repeat, with no
    # commit and no CI round trip in between.
    echo "Packing $source_dir as it stands ..."

    # Both halves, because a switch is now both packages. The payload one
    # refuses to pack without a built façade to put in it - see its
    # EnsurePayloadExists - which is the error you want rather than a package
    # that installs nothing.
    dotnet pack "$source_dir/src/FFAudio.NET/FFAudio.NET.csproj" \
        --configuration Release --output "$staging" --nologo -v quiet
    dotnet pack "$source_dir/packaging/$payload/$payload.csproj" \
        --configuration Release --output "$staging" --nologo -v quiet

    # iOS only if a framework has actually been cross-compiled next door.
    # Skipping is the right default rather than a failure: building it is tens
    # of minutes and an Xcode away, and the loop this flag exists for - edit
    # the library, pack, run Flower's suite - is a desktop loop.
    if [ -n "$ios_payload" ]; then
        if [ -d "$source_dir/native/artifacts/ios/ios-device/ffaudio.framework" ]; then
            dotnet pack "$source_dir/packaging/$ios_payload/$ios_payload.csproj" \
                --configuration Release --output "$staging" --nologo -v quiet
        else
            echo "No iOS framework in $source_dir/native/artifacts/ios - skipping $ios_payload."
            echo "Build one with $source_dir/native/build-all.sh ios, or drop --local to take CI's."
            ios_payload=""
        fi
    fi
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

# The binding specifically. FFAudio.NET.macOS and its four siblings sit beside
# it under names a plain FFAudio.NET.* glob also matches, and picking one of
# those would read its version out of the wrong file name.
package="$(find "$staging" -name 'FFAudio.NET.[0-9]*.nupkg' -not -name '*.snupkg' | head -1)"
[ -n "$package" ] || { echo "No FFAudio.NET package was produced." >&2; exit 1; }

file="$(basename "$package")"
version="${file#FFAudio.NET.}"
version="${version%.nupkg}"

payload_package="$(find "$staging" -name "$payload.[0-9]*.nupkg" -not -name '*.snupkg' | head -1)"
if [ -z "$payload_package" ]; then
    echo
    echo "That build has no $payload package - it carries the binding and no façade." >&2
    if [ "$local_pack" -eq 1 ]; then
        echo "Build one first: $source_dir/native/build-all.sh $platform" >&2
    else
        echo "It predates FFAudio.NET's payload packages; take a newer run." >&2
    fi
    exit 1
fi

# The iOS payload, on a Mac. A CI build carries all five, so its absence there
# means the run predates them rather than that this host cannot use it.
ios_package=""
if [ -n "$ios_payload" ]; then
    ios_package="$(find "$staging" -name "$ios_payload.[0-9]*.nupkg" -not -name '*.snupkg' | head -1)"
    if [ -z "$ios_package" ]; then
        echo
        echo "That build has no $ios_payload package - take a newer run." >&2
        exit 1
    fi
fi

mkdir -p "$feed"
cp "$staging"/*.nupkg "$staging"/*.snupkg "$feed/" 2>/dev/null || cp "$staging"/*.nupkg "$feed/"

# NuGet extracts a package into the global cache once and never looks at the
# .nupkg again, so a rebuilt package at a version already extracted would
# restore as whatever was there before. Re-running the same CI commit is the
# ordinary way to hit that.
cache="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
for id in ffaudio.net $(echo "$payload $ios_payload" | tr '[:upper:]' '[:lower:]'); do
    if [ -d "$cache/$id/$version" ]; then
        echo "Evicting the already-extracted $id $version from the global cache."
        rm -rf "$cache/$id/$version"
    fi
done

sweep_stale_copies

# Scoped to the two iOS heads rather than to Flower, which is where the desktop
# payload goes. A payload is a thing an app ships, and Flower is a library that
# five heads reference - handing it an iOS framework would mean restoring one
# for a Linux build to ignore. The package's own targets file picks the device
# or simulator slice off the RuntimeIdentifier, which is exactly what the lines
# it replaces did by hand.
ios_block=""
if [ -n "$ios_payload" ]; then
    ios_block="
  <ItemGroup Condition=\"'\$(MSBuildProjectName)' == 'Flower.iOS' Or '\$(MSBuildProjectName)' == 'Flower.DeviceChecks.iOS'\">
    <PackageReference Include=\"$ios_payload\" Version=\"$version\" />
  </ItemGroup>
"
fi

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
    <!-- The façade itself, as runtimes/<rid>/native/. The SDK copies it beside
         every binary downstream of here and the macOS head bundles it into
         Contents/MonoBundle, so nothing has to name a path. It reaches the
         phone heads too, through Flower, and lands nowhere: an ios-arm64 or
         android-arm64 build matches no RID folder this package has, which is
         why the mobile payloads are separate packages with a targets file
         rather than more runtimes/ entries. -->
    <PackageReference Include="$payload" Version="$version" />
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
$ios_block
</Project>
PROPS

echo
echo "Flower is now building against FFAudio.NET $version"
echo "  binding : $feed/$file"
echo "  façade  : $feed/$(basename "$payload_package")"
if [ -n "$ios_package" ]; then
    echo "  iOS     : $feed/$(basename "$ios_package")"
fi
echo "  switch  : $props  (gitignored; delete it or run --off to undo)"
echo
# Android still loads libs/<abi>/libffaudio.so, a checked-in binary of Flower's
# own vendored source. Nothing here redirects it, so an Android head built
# while this switch is on pairs new managed code with an older library - and
# the metadata calls are in the Decoder constructor, so that is every open
# failing rather than one feature missing.
echo "Note: Flower.Android still loads its checked-in façade, which predates the"
echo "      package's metadata calls - run that one switched off."
echo
echo "Try it:"
echo "  dotnet test Flower.Tests/Flower.Tests.csproj --filter Category=RequiresFfmpeg"
echo "  dotnet run --project Flower.MacOS/Flower.MacOS.csproj"
if [ -n "$ios_package" ]; then
    echo "  scripts/ios-device-checks.sh"
fi
