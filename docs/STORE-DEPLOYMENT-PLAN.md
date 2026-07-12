# App Store / Play Store Deployment Plan

Goal: get `Flower.iOS`/`Flower.Android` from "runs on a simulator/emulator"
(where `MOBILE-PLAN.md` currently leaves them) to a real, submitted,
approved store listing. This is the phase *after* `MOBILE-PLAN.md` —
several of its still-open "What's left in Phase 3" items are treated below
as submission prerequisites, not optional polish, because they map directly
onto Apple's and Google's completeness/quality review criteria.

## Research summary (verified against the actual project + current, 2026 store policy)

- **Two build-config blockers already exist in the repo, independent of
  anything else in this plan:**
  - `Flower.Android/Flower.Android.csproj` sets
    `<AndroidPackageFormat>apk</AndroidPackageFormat>`. Google Play has
    required the **Android App Bundle (`.aab`)** format for all new apps
    since 2021 — a raw APK cannot be submitted to Play Console at all today.
  - `Flower.iOS/Flower.iOS.csproj` targets `net9.0-ios18.0`. **As of April
    28, 2026, Apple requires every new submission/update to be built with
    the iOS 26 SDK** — .NET 10 is where Apple's "26"-generation SDKs are
    fully supported (net9.0 only got a temporary Xcode 26 lifeline); the
    rest of this solution (`Flower.csproj`, `Flower.Desktop.csproj`) is
    already on net10.0. The fix is a TFM bump to `net10.0-ios26.2` (current
    canonical .NET 10 + iOS 26 TFM), which requires Xcode 26.x on the build
    machine.
- **Neither mobile project has a real version number yet, and both stores
  will reject a submission over it** — Android's `versionCode` / iOS's
  `CFBundleVersion` must be a plain integer that strictly increases with
  every submission; `Flower.Android.csproj` hardcodes a `1` that nothing
  updates, and `Flower.iOS.csproj` sets nothing at all. This is
  `VERSIONING-PLAN.md`'s Phase 3 to fix, not scoped further here.
- **Android target API level is already compliant, no action needed.** Play
  Console requires targeting API 36 (Android 16) by August 31, 2026; a clean
  build's merged manifest already shows `targetSdkVersion="36"` — `net10.0-android`'s
  implied default API level already satisfies this.
- **New personal Google Play developer accounts must pass a closed test
  before any production release**: 12 testers opted in and *actively
  engaging* (Google now measures this, not just opt-in) for 14 continuous
  days. This is a real ~2-week minimum lead time to plan around, and needs
  actual recruited testers, not just placeholders. (Organization accounts
  are exempt, but require their own extra verification — not clearly worth
  it just to skip this.)
- **Apple requires a Privacy Manifest (`PrivacyInfo.xcprivacy`) for every
  .NET-for-iOS app, not just apps using sensitive APIs directly** — the .NET
  runtime/BCL itself uses "required reason" APIs (e.g. file timestamps, disk
  space) regardless of what Flower's own code does, and Apple will reject a
  submission missing this file. Microsoft publishes the exact category list
  a plain .NET-for-iOS app needs to declare; start from that rather than
  guessing.
- **`UIBackgroundModes: audio` is already declared** in `Flower.iOS`'s
  `Info.plist` — background playback continuation on iOS needs no new work.
- **Android has no equivalent** — no foreground service or media-session
  notification exists in `AndroidManifest.xml` today, so playback likely
  stops (or Android eventually kills the process) when the app is
  backgrounded or the screen locks. Not a hard store-rejection reason on its
  own, but a real functional gap that would read as broken/unpolished to
  the first real users and is worth closing before, not after, launch.
- **LibVLC's LGPL license needs a real answer, not silence.** VLC's own
  official iOS app was pulled from the App Store for years over exactly
  this (static linking vs. the App Store's locked-down model), and
  reinstated once VideoLAN moved to **dynamically-linked** libvlc frameworks
  specifically to make LGPL compliance work within Apple's rules — which is
  exactly what the `VideoLAN.LibVLC.iOS`/`LibVLCSharp` packages Flower
  already uses provide. Practical requirement: ship the LGPL license text +
  a notice that source is available (VideoLAN's own upstream repo satisfies
  "source available"). **Done**: `LICENSE` (Apache 2.0, Flower's own) and
  `NOTICE` (per-dependency third-party license/source breakdown, correctly
  naming LibVLCSharp/VideoLAN.LibVLC.* under LGPL-2.1-or-later with source
  links, plus TagLib# and the MIT-licensed set) are committed at the repo
  root.
- **Both stores require a privacy policy URL**, even for an app that
  collects no data and phones home to nowhere (Flower's only "network"
  activity today is local mDNS sync between a user's own devices — see
  `SYNC-PLAN.md` — and the file logging added this session never leaves the
  device). A one-page static privacy policy is enough; GitHub Pages off this
  same repo is a natural, zero-extra-infrastructure host for it.
- **Completeness risk carried over from `MOBILE-PLAN.md`'s open items — now
  resolved.** Apple's App Review Guideline 2.1 ("Incomplete App
  Information"/apps that feel unfinished) is a common real rejection reason;
  the specific gaps this plan flagged (blank empty-library screen, no
  permission-retry/rescan path, and — beyond what was originally
  review-blocking — the now-playing sheet/playlist management/search/track-info
  page too) are all built now. See `MOBILE-PLAN.md`'s Phase 3 status and
  Phase 3 below.

---

## Phase 0: Accounts, signing, legal housekeeping

**Plan:**
1. Enroll in the Apple Developer Program ($99/yr) and register a Google
   Play Console account ($25 one-time).
2. iOS: generate a Distribution certificate + App Store provisioning
   profile in App Store Connect. `Flower.iOS.csproj` already has
   `ProvisioningType=automatic` and `RuntimeIdentifier=ios-arm64` (device)
   scaffolded — this mostly needs a real paid-account identity behind it,
   not new project config.
3. Android: let Play App Signing manage the app signing key (Google's
   recommended default) and keep your own upload key separately.
4. ~~Commit the existing untracked `LICENSE`/`NOTICE` files~~ — **done**,
   both committed at the repo root and confirmed to correctly name
   LibVLC/VideoLAN with source links (see Research summary above).
5. Write and host a one-page privacy policy (GitHub Pages off this repo is
   the natural host) — content is simple given Flower collects nothing that
   leaves the device today.

**Effort:** Small, mostly account/waiting-on-approval time. **Risk:** Low,
but non-technical steps (Apple's account verification, Play Console
identity verification) can themselves take days and should be started
first, in parallel with everything else.

---

## Phase 1: Fix the two build-config blockers

**Plan:**
1. `Flower.Android.csproj`: change `<AndroidPackageFormat>apk</AndroidPackageFormat>`
   to `aab`, do a clean `dotnet publish` and confirm a real `.aab` is
   produced.
2. `Flower.iOS.csproj`: bump `<TargetFramework>` from `net9.0-ios18.0` to
   `net10.0-ios26.2`, install Xcode 26.x on the build machine, and do a
   clean device build (`RuntimeIdentifier=ios-arm64`) to catch any API
   breakage from the jump before anything else depends on it.

**Effort:** Small–Medium (the iOS SDK bump is the one with real breakage
risk, same category CROSS-PLATFORM-PLAN.md flagged for the earlier net7→net8
Android bump). **Risk:** Medium for iOS specifically — two major SDK
generations in one jump (18→26) is more likely to surface something than
the Android bump was.

---

## Phase 2: Add the iOS Privacy Manifest

**Plan:**
1. Add `PrivacyInfo.xcprivacy` to `Flower.iOS`, starting from Microsoft's
   published required-reason-API category list for a plain .NET-for-iOS
   app (covers what the BCL itself needs) — reference
   [Microsoft's guidance](https://learn.microsoft.com/en-us/dotnet/maui/ios/privacy-manifest)
   directly rather than re-deriving the category list from scratch.
2. Check whether `LibVLCSharp`/`VideoLAN.LibVLC.iOS` ship their own privacy
   manifest already (increasingly common for widely-used native SDKs) —
   if not, their required-reason API usage (if any) needs declaring too.
3. Wire the file into the app bundle per Microsoft's documented `<ItemGroup>`
   project-file entry.

**Effort:** Small. **Risk:** Low, but skipping this is an automatic
rejection, not a warning — do not treat as optional.

---

## Phase 3: Close the completeness gaps carried over from `MOBILE-PLAN.md` — done

All of it: empty-library state (a real "add music" empty state, not a blank
screen), a way to see/retry a denied Android media permission and trigger a
rescan from the mobile UI, and the rest of `MOBILE-PLAN.md`'s former
"What's left in Phase 3" list (now-playing sheet, playlist management,
search/filter, track info as a page) — see `MOBILE-PLAN.md`'s Phase 3 status
for what shipped. Nothing left in this phase.

---

## Phase 4: Android background playback

**Problem:** no foreground service/media-session notification exists;
playback likely doesn't survive backgrounding or screen-lock today.

**Plan:** add a foreground `MediaSessionService`-backed playback service
with a media-style notification (standard Android pattern for any music
app), wired to the existing `IAudioManager`/`PlaylistControlViewModel`
so play/pause/next controls are reachable from the lock screen and
notification shade.

**Effort:** Medium. **Risk:** Low–Medium — new platform-specific surface
area, but a very standard, well-documented pattern for this exact use case.

---

## Phase 5: Store listing assets + submission

**Plan:**
1. Icons at each required resolution (both stores enforce exact size sets),
   screenshots per required device-size bucket, description, support URL,
   the privacy policy URL from Phase 0.
2. Complete Play Console's Data Safety questionnaire and Apple's App
   Privacy labels — both should be straightforward "we don't collect
   anything" given Flower's actual data flows, but must be filled out
   accurately regardless.
3. **Android:** start the mandatory closed test (12 testers, 14 consecutive
   days, actively opening the app) as early as Phase 1 is stable enough to
   install — this is the single biggest fixed-duration item in the whole
   plan and should not be the last thing scheduled.
4. **iOS:** run at least one TestFlight beta pass before the first App
   Store Review submission — catches obvious rejections cheaply before
   they cost a review cycle (typically 24–48h turnaround, but a rejection
   restarts that clock).
5. Submit to both. Expect at least one rejection round to be normal, not a
   sign of a bad submission.

**Effort:** Medium, mostly asset production + the fixed 14-day Android
testing window. **Risk:** Low technically; the main risk is scheduling —
the Android testing window and Apple's review turnaround are both
wall-clock time that doesn't compress no matter how fast the code is ready.

---

## Suggested execution order

1. **Phase 0** (accounts) — start immediately, in parallel with everything
   else; account verification is the least predictable wall-clock item.
2. **Phase 1** (fix the two build blockers) — nothing else can be tested
   end-to-end until these land.
3. **Phase 2** (iOS privacy manifest) — small, do right after Phase 1's iOS
   SDK bump while already in that project.
4. ~~**Phase 3** (completeness gaps) — needed before either store submission
   looks credible.~~ — **done**, nothing left to sequence here.
5. **Phase 4** (Android background playback) — can run in parallel with
   Phase 3; different codepath.
6. **Kick off Android's 12-tester/14-day closed test as soon as Phases
   1+3+4 are stable enough to install**, even before iOS is fully ready —
   it's pure elapsed time, so starting it early is free.
7. **Phase 5** (listing + submission) — iOS TestFlight pass, then both
   submissions.

Sources consulted: [Apple Developer Program / App Store submission
guidance](https://developer.apple.com/support/terms/apple-developer-program-license-agreement/),
[.NET MAUI/`.NET`-for-iOS privacy manifest docs](https://learn.microsoft.com/en-us/dotnet/maui/ios/privacy-manifest?view=net-maui-10.0),
[Apple's April 2026 SDK submission requirement coverage](https://gotechsolutions.co/blog/apple-app-store-submission-guide-2026/),
[.NET 10 / iOS 26 support matrix](https://www.telerik.com/blogs/net-10-ios-updates-notes-net-maui-developers),
[Google Play's closed-testing requirement for new personal accounts](https://support.google.com/googleplay/android-developer/answer/14151465?hl=en),
[Android 16 (API 36) target requirement](https://stora.sh/blog/2026-04-14-android-api-36-august-deadline-what-to-do-now),
and general LGPL/dynamic-linking/App Store precedent for LibVLC.
