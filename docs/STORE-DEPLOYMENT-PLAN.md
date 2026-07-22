# App Store / Play Store Deployment Plan

Goal: get `Flower.iOS`/`Flower.Android` from runnable (`MOBILE-PLAN.md`) to submitted and approved.

## Key findings

- **Two build-config blockers, open:** `Flower.Android.csproj` produces `.apk` — Play Console requires `.aab`. `Flower.iOS.csproj` targets `net9.0-ios18.0` — Apple requires the iOS 26 SDK (`net10.0-ios26.2`) for submissions since April 2026.
- **No real version numbers yet** — Android `versionCode`/iOS `CFBundleVersion` need a strictly-increasing integer; neither project has one. Scoped to `VERSIONING-PLAN.md` Phase 3, not here.
- Android target API level (36) is already compliant — no action needed.
- **New personal Google Play accounts need a closed test first:** 12 actively-engaging testers for 14 continuous days — a real ~2-week lead time to plan around.
- **Apple requires a Privacy Manifest** (`PrivacyInfo.xcprivacy`) for every .NET-for-iOS app, even one using no sensitive APIs directly (the BCL itself triggers "required reason" API categories). Missing it is an automatic rejection.
- `UIBackgroundModes: audio` is already declared on iOS — background playback needs no new work there. **Android has no equivalent** (no foreground service/media-session notification) — playback likely doesn't survive backgrounding today.
- **LibVLC LGPL compliance: done.** `LICENSE`/`NOTICE` are committed at the repo root, correctly naming LibVLCSharp/VideoLAN.LibVLC.* as LGPL with source links. VideoLAN's dynamically-linked iOS framework (which Flower already uses) is what makes this workable inside App Store rules.
- Both stores need a privacy policy URL — trivial content given Flower's only network activity is local mDNS sync between a user's own devices.
- **Completeness gaps from `MOBILE-PLAN.md` (empty state, permission-retry, now-playing sheet, playlist management, search/filter, track-info page): all done.**

---

## Phase 0: Accounts, signing, legal — mostly done

Enroll in Apple Developer Program ($99/yr) + Google Play Console ($25 one-time). iOS: Distribution cert + provisioning profile (project already scaffolds `ProvisioningType=automatic`/`ios-arm64`). Android: let Play App Signing manage the signing key. Write/host a one-page privacy policy (GitHub Pages off this repo works). `LICENSE`/`NOTICE` — done.

## Phase 1: Fix the two build-config blockers — open

1. `Flower.Android.csproj`: `AndroidPackageFormat` → `aab`, verify a clean publish produces one.
2. `Flower.iOS.csproj`: bump TFM to `net10.0-ios26.2`, requires Xcode 26.x; do a clean device build to catch API breakage from the two-generation SDK jump (medium risk — bigger jump than the earlier Android net7→net8 bump).

## Phase 2: iOS Privacy Manifest — open

Add `PrivacyInfo.xcprivacy` starting from Microsoft's published required-reason-API category list for .NET-for-iOS apps; check whether `LibVLCSharp`/`VideoLAN.LibVLC.iOS` already ship their own. Small effort, but skipping it is an automatic rejection.

## Phase 3: Completeness gaps — done

See `MOBILE-PLAN.md` Phase 3.

## Phase 4: Android background playback — open

No foreground service/media-session notification exists. Add a foreground `MediaSessionService`-backed playback service with a media-style notification, wired to the existing `IAudioManager`/`PlaylistControlViewModel` — standard pattern for this use case.

## Phase 5: Store listing + submission — open

Icons/screenshots/description/support URL/privacy policy → Play Data Safety questionnaire + Apple Privacy labels (both should be "we collect nothing") → start Android's 12-tester/14-day closed test as early as Phase 1 is installable (biggest fixed-duration item, schedule first) → at least one iOS TestFlight pass before first App Store submission → submit both, expect at least one rejection round as normal.

---

## Suggested order

Phase 0 (parallel with everything, least predictable wall-clock) → Phase 1 (blocks all end-to-end testing) → Phase 2 (do right after Phase 1's iOS bump) → Phase 4 (parallel with nothing left in Phase 3) → kick off Android's closed test as soon as 1+4 are installable → Phase 5 (TestFlight, then both submissions).
