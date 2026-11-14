# App Store / Play Store Deployment Plan

Goal: get `Flower.iOS`/`Flower.Android` from runnable to submitted and approved. Getting them runnable in the first place was `MOBILE-PLAN.md`, which finished and was deleted; what it had left over is Phase 3 below.

## Key findings

- **Build-config blockers: none left.** A Release build of `Flower.Android` produces the `.aab` Play Console requires (Debug stays `.apk`, for `dotnet run` and `adb install`), and `Flower.iOS` targets `net10.0-ios27.0`, the SDK Apple requires since April 2026.
- **No real build numbers yet** — Android `versionCode`/iOS `CFBundleVersion` need a strictly-increasing integer, and both csproj files carry a literal `1` as a dev placeholder. The marketing version *is* real (MinVer stamps it from git tags), but the build number is a mobile release-pipeline concern: use `git tag --list 'v*' | wc -l` with the `/p:ApplicationVersion=<n>` override, and confirm the number actually beats the last accepted submission.
- Android target API level (36) is already compliant — no action needed.
- **New personal Google Play accounts need a closed test first:** 12 actively-engaging testers for 14 continuous days — a real ~2-week lead time to plan around.
- **Apple requires a Privacy Manifest** (`PrivacyInfo.xcprivacy`) for every .NET-for-iOS app, even one using no sensitive APIs directly (the BCL itself triggers "required reason" API categories). Missing it is an automatic rejection.
- `UIBackgroundModes: audio` is already declared on iOS — background playback needs no new work there. **Android has no equivalent** (no foreground service/media-session notification) — playback likely doesn't survive backgrounding today.
- **LGPL compliance: settled (November 2026).** FFmpeg is LGPL-2.1-or-later and statically linked into FFAudio.NET's façade on macOS, Linux, iOS and Android (`libffaudio.dylib`/`.so`, `ffaudio.framework`); only Windows ships it as separate DLLs. What the LGPL asks of a static link is that a user can put their own FFmpeg in, and that is met by the façade being a separate shared library in every app, built from public source (`yanos/FFAudio.NET`, tagged per version, `native/build-all.sh` and `native/codec-set.sh`), so it can be rebuilt against any FFmpeg and swapped in - on a phone, re-signed with the user's own key. `NOTICE` says all of that per platform, names where the FFmpeg source is and offers it on request; it no longer mentions LibVLC, and it lists every component that ships. The licence texts travel inside every build, the server's included (`OpenSourceNotices`, embedded in `Flower.Core.dll`), and are shown under "Open-Source Licences": the About window on a desktop, the foot of Settings on a phone, the foot of the sidebar in a browser, which opens the server's `GET /licences`; the server's publish (so its Docker image) also carries `NOTICE`, `LICENSE` and `licenses/` as files beside `Flower.Server.dll`. Windows' FFmpeg DLLs used to be a third-party binary build of an FFmpeg git commit, not the 9.0.2 release `NOTICE` names as the corresponding source; since FFAudio.NET 1.0.3 (`FFAudioPackageVersion` now pins 1.1.2) they are built from that release with MSVC (`native/windows/build-ffmpeg.sh`, run by `build.ps1`), and its `NOTICE` no longer claims macOS and Linux link FFmpeg dynamically. FFAudio.NET 1.0.0-1.0.2 are still on NuGet with the old DLLs; unlisting them is the owner's call, since nothing of Flower's ships them any more. **Still the owner's call, not an engineering one:** whether the App Store's own terms sit comfortably with LGPL code is argued both ways, though plenty of apps built on LGPL FFmpeg ship there.
- Both stores need a privacy policy URL. `docs/PRIVACY.md` is that policy (crash reports, logs to the owner's own server, the third-party lookups), linkable on GitHub.
- **Completeness gaps (empty state, permission-retry, now-playing sheet, playlist management, search/filter, track-info page): all done** — see Phase 3, which also carries the two real-device verification items still outstanding.

---

## Phase 0: Accounts, signing, legal — mostly done

Enroll in Apple Developer Program ($99/yr) + Google Play Console ($25 one-time). iOS: Distribution cert + provisioning profile (project already scaffolds `ProvisioningType=automatic`/`ios-arm64` — though note `ProvisioningType` is read by Rider and Visual Studio, not by MSBuild, which simply signs with the best profile already installed; `scripts/register-ios-device.sh` exists because nothing in the build itself will create one). Android: let Play App Signing manage the signing key. Write/host a one-page privacy policy (GitHub Pages off this repo works). `LICENSE`/`NOTICE` — done, and redone for FFmpeg in November 2026 (see the LGPL finding above).

## Phase 1: Fix the build-config blockers — done

1. ~~`Flower.Android.csproj`: `AndroidPackageFormat` → `aab`~~ — done for Release (November 2026); `dotnet publish -c Release` produces `com.yanos.flower-Signed.aab`.
2. ~~`Flower.iOS.csproj`: bump the TFM~~ — done; it is `net10.0-ios27.0`.

## Phase 2: iOS Privacy Manifest — done

`Flower.iOS/PrivacyInfo.xcprivacy` (November 2026), a `BundleResource` at the bundle's root. It declares the required-reason APIs the .NET runtime and BCL use (file timestamps C617.1 and 3B52.1, system boot time 35F9.1, disk space E174.1) and, as collected data, what crash reporting sends: crash data, performance data and other diagnostic data, none linked to the user or used for tracking, for app functionality. The vendored native frameworks (`ffaudio`, `miniaudio`, `flowerbridge`, SkiaSharp) ship none of their own and call none of those APIs. Not yet checked by App Store Connect itself: the first TestFlight upload is where a missing reason would show up (as an ITMS-91053 email). The App Store privacy label (Phase 5) has to say the same as the manifest, and so does `docs/PRIVACY.md`: change all three together.

## Phase 3: Completeness gaps — done, bar two verification items

Every gap is built: empty state, permission-retry, now-playing sheet, playlist
management, search/filter, track info. Validated against 5 real albums / 33
tracks on an Android emulator, an iOS simulator and a real iPhone.

Two items survive from `MOBILE-PLAN.md`, which is otherwise finished and gone,
and both are prerequisites for a submission rather than for running the app:

1. **Android album art on real hardware.** Confirmed on iOS and on the Android
   emulator; never seen on a physical Android device.
2. **A committable fixture set.** The 5-album test library was pulled from a
   real Apple Music collection and is not in the repo, so nobody else can
   reproduce the validation above. Replace it with something small and
   royalty-free.

## Phase 4: Android background playback — open

No foreground service/media-session notification exists. Add a foreground `MediaSessionService`-backed playback service with a media-style notification, wired to the existing `IAudioManager`/`PlaylistControlViewModel` — standard pattern for this use case.

## Phase 5: Store listing + submission — open

Icons/screenshots/description/support URL/privacy policy → Play Data Safety questionnaire + Apple Privacy labels (both: crash and diagnostic data, not linked, not tracking, as `PrivacyInfo.xcprivacy` declares; nothing else) → start Android's 12-tester/14-day closed test as early as Phase 1 is installable (biggest fixed-duration item, schedule first) → at least one iOS TestFlight pass before first App Store submission → submit both, expect at least one rejection round as normal.

---

## Suggested order

Phase 0 (parallel with everything, least predictable wall-clock) → ~~Phase 1~~, ~~Phase 2~~ (done) → Phase 4 (parallel with nothing left in Phase 3) → kick off Android's closed test as soon as 1+4 are installable → Phase 5 (TestFlight, then both submissions).
