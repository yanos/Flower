#!/bin/bash
# Packages a desktop head as the installer a release carries:
#
#   scripts/package-desktop.sh dmg        # on a Mac:     Flower-<version>-macos-arm64.dmg
#   scripts/package-desktop.sh appimage   # on Linux:     Flower-<version>-linux-x64.AppImage
#   scripts/package-desktop.sh setup      # on Windows:   Flower-<version>-win-x64-Setup.exe
#   scripts/package-desktop.sh dmg <dir>  # ...into <dir> rather than artifacts/installers
#
# `.github/workflows/tests.yml` runs each on its own runner when a v* tag is
# pushed and attaches all three to that tag's GitHub release. Run it by hand
# to try a package before tagging; the version comes from MinVer, as it does
# for every build, so an untagged tree packages as 0.4.1-alpha.0.<height>.
#
# Each runs only on its own OS, for two reasons that do not go away. The
# FFmpeg payload is chosen by the host, not the target (Flower.csproj), so a
# Linux package built on a Mac would carry the Mac's libffaudio and decode
# nothing. And Velopack's packaging is OS-native.
#
# The dmg is Flower.MacOS, the head with AVKit and AppKit, as a drag-to-
# Applications disk image; it is Apple Silicon only because FFAudio.NET.macOS
# ships osx-arm64 and nothing else. The other two are Flower.Desktop packed by
# Velopack (vpk, pinned in .config/dotnet-tools.json), the updater
# docs/AUTO-UPDATE-PLAN.md chose - Setup.exe installs per user with no admin
# prompt, and the AppImage runs where it is. Building the AppImage needs
# mksquashfs (`apt install squashfs-tools`). Nothing is signed: the dmg's
# bundle carries the ad hoc signature the SDK gives every arm64 build, so a
# downloaded copy opens through System Settings > Privacy & Security > Open
# Anyway, and Setup.exe gets SmartScreen's "More info > Run anyway".

set -euo pipefail

kind="${1:-}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="${2:-$repo/artifacts/installers}"

case "$kind" in
    dmg)      os=Darwin;  project=Flower.MacOS;   rid=osx-arm64 ;;
    appimage) os=Linux;   project=Flower.Desktop; rid=linux-x64 ;;
    setup)    os=Windows; project=Flower.Desktop; rid=win-x64 ;;
    *)
        sed -n '2,7p' "$0" | sed 's/^# \{0,1\}//'
        exit 2
        ;;
esac

host="$(uname -s)"
case "$host" in
    MINGW*|MSYS*|CYGWIN*) host=Windows ;;
esac
if [[ "$host" != "$os" ]]; then
    echo "error: a $kind is built on $os, and this is $host - see this script's header" >&2
    exit 1
fi

csproj="$repo/$project/$project.csproj"
mkdir -p "$out"
out="$(cd "$out" && pwd)"

# MinVer's own answer rather than the tag re-read here, because it is the
# computation the build stamps into the assemblies and so cannot disagree with
# it. -target:MinVer needs a restore first or fails MSB4057.
dotnet restore "$csproj" -r "$rid" >/dev/null
version="$(dotnet msbuild "$csproj" -target:MinVer -getProperty:Version -p:RuntimeIdentifier="$rid" | tr -d "\r")"
echo "Packaging $project $version ($rid) as a $kind"

# The Release output is cleared first. A build directory carried over from an
# earlier build once produced a bundle missing libffaudio.dylib - an app that
# opens and plays nothing - which a clean publish never does.
rm -rf "$repo/$project/bin/Release" "$repo/$project/obj/Release"

work="$repo/$project/obj/package-$kind"
rm -rf "$work"
mkdir -p "$work"

case "$kind" in
    dmg)
        # CFBundleShortVersionString is numbers only, so a pre-release's
        # suffix is dropped there; the full version is still in the file
        # name and in every assembly. CreatePackage=false: publish would
        # otherwise also build a .pkg nobody asked for.
        numeric="${version%%[-+]*}"
        dotnet publish "$csproj" -c Release -r "$rid" \
            -p:CreatePackage=false \
            -p:ApplicationDisplayVersion="$numeric" \
            -p:ApplicationVersion="$numeric"

        # Flower.MacOS.app on disk because the IDEs need that name (see
        # _AppBundleName in the csproj); the copy someone drags into
        # Applications is Flower.app. Renaming the folder leaves the
        # signature intact, which the verify below holds it to.
        bundle="$repo/$project/bin/Release/net10.0-macos/$rid/$project.app"
        stage="$work/stage"
        mkdir -p "$stage"
        ditto "$bundle" "$stage/Flower.app"
        codesign --verify --deep --strict "$stage/Flower.app"
        if [[ ! -f "$stage/Flower.app/Contents/MonoBundle/libffaudio.dylib" ]]; then
            echo "error: the bundle has no libffaudio.dylib, so it would play nothing" >&2
            exit 1
        fi
        ln -s /Applications "$stage/Applications"

        dmg="$out/Flower-$version-macos-arm64.dmg"
        hdiutil create -volname Flower -srcfolder "$stage" -fs HFS+ -format UDZO -ov "$dmg"
        echo "Wrote $dmg"
        ;;

    appimage|setup)
        publish="$work/publish"
        dotnet publish "$csproj" -c Release -r "$rid" --self-contained -o "$publish"

        if [[ "$kind" == setup ]]; then
            exe=Flower.Desktop.exe
            icon="$repo/Flower/Assets/flower-icon.ico"
            native=ffaudio.dll
        else
            exe=Flower.Desktop
            icon="$repo/Flower/Assets/flower-icon.png"
            native=libffaudio.so
        fi
        if [[ ! -f "$publish/$native" ]]; then
            echo "error: the publish has no $native, so it would play nothing" >&2
            exit 1
        fi

        # The id is Flower.Desktop rather than Flower because it names the
        # folder Setup.exe installs into, %LocalAppData%\<id> - and
        # %LocalAppData%\Flower is where Flower keeps the library and the
        # settings (AppDataDirectory). Velopack deletes its folder on
        # uninstall, so sharing it would take the library with the app. The
        # title is what the shortcuts and Apps & features say.
        (cd "$repo" && dotnet tool restore >/dev/null)
        releases="$work/releases"
        (cd "$repo" && dotnet vpk pack \
            --packId Flower.Desktop \
            --packTitle Flower \
            --packAuthors Flower \
            --packVersion "$version" \
            --packDir "$publish" \
            --runtime "$rid" \
            --mainExe "$exe" \
            --icon "$icon" \
            --outputDir "$releases")

        if [[ "$kind" == setup ]]; then
            built=("$releases"/*-Setup.exe)
            target="$out/Flower-$version-win-x64-Setup.exe"
        else
            built=("$releases"/*.AppImage)
            target="$out/Flower-$version-linux-x64.AppImage"
        fi
        if [[ ${#built[@]} -ne 1 || ! -f "${built[0]}" ]]; then
            echo "error: expected one installer in $releases, found: ${built[*]}" >&2
            exit 1
        fi
        cp "${built[0]}" "$target"
        echo "Wrote $target"
        ;;
esac
