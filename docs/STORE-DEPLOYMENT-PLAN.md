# App Store / Play Store Deployment Plan

Goal: get `Flower.iOS`/`Flower.Android` from runnable to submitted and approved. Getting them runnable in the first place was `MOBILE-PLAN.md`, which finished and was deleted; what it had left over is Phase 3 below.

## Key findings

- **Two build-config blockers, open:** `Flower.Android.csproj` produces `.apk` — Play Console requires `.aab`. `Flower.iOS.csproj` targets `net9.0-ios18.0` — Apple requires the iOS 26 SDK (`net10.0-ios26.2`) for submissions since April 2026.
- **No real version numbers yet** — Android `versionCode`/iOS `CFBundleVersion` need a strictly-increasing integer, and both csproj files carry a literal `1` as a dev placeholder. The marketing version *is* real (MinVer stamps it from git tags), but the build number is a release-pipeline concern: `AUTO-UPDATE-PLAN.md` Phase 4 owns the `git tag --list 'v*' | wc -l` recipe and the `/p:ApplicationVersion=<n>` override. What belongs here is only confirming the number actually beats the last accepted submission.
- Android target API level (36) is already compliant — no action needed.
- **New personal Google Play accounts need a closed test first:** 12 actively-engaging testers for 14 continuous days — a real ~2-week lead time to plan around.
- **Apple requires a Privacy Manifest** (`PrivacyInfo.xcprivacy`) for every .NET-for-iOS app, even one using no sensitive APIs directly (the BCL itself triggers "required reason" API categories). Missing it is an automatic rejection.
- `UIBackgroundModes: audio` is already declared on iOS — background playback needs no new work there. **Android has no equivalent** (no foreground service/media-session notification) — playback likely doesn't survive backgrounding today.
- **LGPL compliance: needs redoing, and it is not the same problem it was.** `LICENSE`/`NOTICE` at the repo root name LibVLCSharp/VideoLAN.LibVLC.* — components the app no longer ships. What it ships instead is FFmpeg, **statically linked** into `flower_ffmpeg.framework` (iOS) and `libflower_ffmpeg.so` (Android), which is a materially harder LGPL position than VideoLAN's dynamically-linked framework was: static linking obliges shipping whatever a user needs to relink the app against a modified FFmpeg. Read `native/ffmpeg/README.md` — it carries the LGPL-only build constraint (no `--enable-gpl`, no `--enable-nonfree`) — and settle this before either submission, not after.
- Both stores need a privacy policy URL — trivial content given Flower's only network activity is local mDNS sync between a user's own devices.
- **Completeness gaps (empty state, permission-retry, now-playing sheet, playlist management, search/filter, track-info page): all done** — see Phase 3, which also carries the two real-device verification items still outstanding.

---

## Phase 0: Accounts, signing, legal — mostly done

Enroll in Apple Developer Program ($99/yr) + Google Play Console ($25 one-time). iOS: Distribution cert + provisioning profile (project already scaffolds `ProvisioningType=automatic`/`ios-arm64`). Android: let Play App Signing manage the signing key. Write/host a one-page privacy policy (GitHub Pages off this repo works). `LICENSE`/`NOTICE` — done.

## Phase 1: Fix the two build-config blockers — open

1. `Flower.Android.csproj`: `AndroidPackageFormat` → `aab`, verify a clean publish produces one.
2. `Flower.iOS.csproj`: bump TFM to `net10.0-ios26.2`, requires Xcode 26.x; do a clean device build to catch API breakage from the two-generation SDK jump (medium risk — bigger jump than the earlier Android net7→net8 bump).

## Phase 2: iOS Privacy Manifest — open

Add `PrivacyInfo.xcprivacy` starting from Microsoft's published required-reason-API category list for .NET-for-iOS apps; the vendored native frameworks (`flower_ffmpeg`, `miniaudio`) are built in-repo and ship none of their own, so everything the manifest declares is Flower's to declare. Small effort, but skipping it is an automatic rejection.

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

Icons/screenshots/description/support URL/privacy policy → Play Data Safety questionnaire + Apple Privacy labels (both should be "we collect nothing") → start Android's 12-tester/14-day closed test as early as Phase 1 is installable (biggest fixed-duration item, schedule first) → at least one iOS TestFlight pass before first App Store submission → submit both, expect at least one rejection round as normal.

---

## Suggested order

Phase 0 (parallel with everything, least predictable wall-clock) → Phase 1 (blocks all end-to-end testing) → Phase 2 (do right after Phase 1's iOS bump) → Phase 4 (parallel with nothing left in Phase 3) → kick off Android's closed test as soon as 1+4 are installable → Phase 5 (TestFlight, then both submissions).
