# Media Key / OS "Now Playing" Integration Plan

Source: investigation into hardware media-key support (play/pause,
next/previous) prompted by "can media keys be used to play and skip songs on
macOS, Linux and Windows." No existing code for this anywhere in Flower today
(confirmed — nothing named `MediaKey`, `MPRIS`, `MPRemoteCommand`,
`MPNowPlaying`, `SystemMediaTransportControls`, `RegisterHotKey`, or
`GlobalHotKey` appears in the codebase).

## Why this is one plan, not three

There is no cross-platform media-key API — each OS routes hardware/lock-screen/
headset-button commands through its own "Now Playing" system service, and
registering with that service is *also* how the OS gets the track info it
shows on the lock screen, in Control Center, in the notification shade, or on
a car head unit. So "media key support" and "OS now-playing/lock-screen
integration" are the same feature, not two — implementing one piece
(publishing metadata + play state) is most of the work for the other
(receiving commands back). That's true on **all five** platforms Flower
targets, not just desktop, so this plan covers iOS/Android too rather than
treating it as desktop-only.

The four native mechanisms involved:

| Platform | Mechanism | Notes |
|---|---|---|
| macOS | `MPRemoteCommandCenter` / `MPNowPlayingInfoCenter` (MediaPlayer framework) | Modern sanctioned path. The old trick of intercepting raw `NX_KEYTYPE_PLAY` `NSEvent`s still works but needs Accessibility permission and is increasingly unreliable — not worth building on. |
| iOS | Same `MPRemoteCommandCenter` / `MPNowPlayingInfoCenter` API family as macOS | Same framework, same calls — one shared Apple-platform implementation covers both, see Phase 2. |
| Windows | `SystemMediaTransportControls` (WinRT) | First-class WinRT projection, reasonably approachable from .NET without P/Invoke. |
| Linux | **MPRIS** (`org.mpris.MediaPlayer2.Player`) over D-Bus | The desktop environment (GNOME/KDE/etc.) owns actual key capture and forwards presses to whichever app has registered as the active MPRIS player. Avalonia's Linux backend already transitively pulls in `Tmds.DBus.Protocol` (via `Avalonia.FreeDesktop`, seen in the build output for clipboard/file-picker support) — Flower can reuse that D-Bus plumbing rather than adding a new dependency, it just needs to implement and register the MPRIS interface on top of it. |
| Android | `MediaSession` (AndroidX media) | Same idea as SMTC/MPRIS: publish metadata + playback state, receive transport commands. Also what makes Bluetooth headset buttons and Android Auto work. |

None of this is optional per-OS behavior to skip — a user with a Bluetooth
headset, a car head unit, or just their keyboard's F7/F8/F9 keys expects any
media app to respond, and not doing so reads as broken rather than as a
missing nice-to-have.

---

## Phase 1 — Windows (`SystemMediaTransportControls`)

**Problem:** No now-playing/media-key integration at all; Windows users get
no lock-screen/taskbar transport controls and media keys do nothing.

**Plan:**
1. Add a small `IPlatformNowPlaying` seam (name TBD to match the existing
   `IPlatformShell` pattern from `CROSS-PLATFORM-PLAN.md` #2), registered
   per-platform in `App.axaml.cs`'s DI container alongside the other
   singletons.
2. Windows implementation wraps `Windows.Media.SystemMediaTransportControls`:
   set `DisplayUpdater` fields (title/artist/thumbnail) from the currently
   playing `Track` whenever `PlaylistControlViewModel.CurrentlyPlayingTrack`
   changes, update `PlaybackStatus` from `IAudioManager.Playing`/`Paused`/
   `Stopped` events, and subscribe to `ButtonPressed` to call
   `PlaylistControlViewModel.PlayOrPause()`/`Next()`/`Previous()` — all three
   already exist and are the exact hooks needed, no changes to that class
   required.
3. Verify taskbar thumbnail transport controls (the mini play/pause/skip
   buttons Windows shows on hover) and the lock-screen/Action Center now-
   playing card both reflect state correctly, not just the hardware keys.

**Effort:** Small–Medium. **Risk:** Low — purely additive, first-class WinRT
API with no interop friction.

---

## Phase 2 — macOS + iOS (`MPRemoteCommandCenter` / `MPNowPlayingInfoCenter`)

**Problem:** Same gap, and on iOS in particular there is currently no lock-
screen/Control Center now-playing card at all — nothing publishes to
`MPNowPlayingInfoCenter` today.

**Plan:**
1. One shared Apple-platform implementation of `IPlatformNowPlaying` (the
   MediaPlayer framework API is identical on macOS and iOS), living wherever
   Flower's Apple-specific interop already lives (this is a natural
   companion to whatever native seam `AIRPLAY-BLUETOOTH-PLAN.md` Phase 2
   introduces for `AVAudioEngine`/`AVRoutePickerView` — worth landing after
   or alongside that work rather than as two separate native-interop efforts
   on the same platforms).
2. On track/state change, update `MPNowPlayingInfoCenter.default().nowPlayingInfo`
   (title, artist, album, duration, elapsed time, artwork) from the same
   `PlaylistControlViewModel`/`Track` data Phase 1 uses.
3. Register `MPRemoteCommandCenter.shared()`'s `playCommand`/`pauseCommand`/
   `togglePlayPauseCommand`/`nextTrackCommand`/`previousTrackCommand` handlers,
   each calling straight into `PlaylistControlViewModel.PlayOrPause()`/
   `Next()`/`Previous()`.
4. iOS additionally needs the app to declare the `audio` background mode
   (`UIBackgroundModes`) so playback (and command handling) continues once
   the app is backgrounded — currently absent per `MOBILE-PLAN.md`'s finding
   that no mobile audio-session code exists yet. This overlaps with
   `AIRPLAY-BLUETOOTH-PLAN.md` Phase 2's `AVAudioSession` setup; do both in
   the same pass rather than configuring the audio session twice.

**Effort:** Medium. **Risk:** Low on macOS (well-trodden API); iOS carries
the same "first real mobile audio verification" risk already flagged in
`CROSS-PLATFORM-PLAN.md` #4/#7 and `AIRPLAY-BLUETOOTH-PLAN.md` Phase 2.

---

## Phase 3 — Linux (MPRIS over D-Bus)

**Problem:** Same gap; on Linux, media keys are meaningless to an app unless
it registers an MPRIS D-Bus service.

**Plan:**
1. Implement `org.mpris.MediaPlayer2` and `org.mpris.MediaPlayer2.Player`
   as a D-Bus service, exported under a well-known name
   (`org.mpris.MediaPlayer2.Flower`) using the `Tmds.DBus` client already
   present transitively via `Avalonia.FreeDesktop` — confirm whether that
   transitive reference is close enough to a direct `Tmds.DBus.Core`
   dependency to build a service on top of, or whether an explicit
   `Tmds.DBus`/`Tmds.DBus.Protocol` package reference needs adding directly
   to `Flower.csproj` (likely yes, since exporting a service is a different
   code path than the client-side usage Avalonia itself needs).
2. Implement the required `Player` properties (`PlaybackStatus`, `Metadata`,
   `Position`, etc.) backed by the same `PlaylistControlViewModel`/
   `IAudioManager` state as the other two platforms, and the `Play`/`Pause`/
   `PlayPause`/`Next`/`Previous` methods calling the same three
   `PlaylistControlViewModel` methods.
3. Desktop environments differ in exactly which MPRIS properties/signals
   they read (GNOME Shell's media-controls widget vs. KDE Plasma's are not
   perfectly consistent) — smoke-test against at least GNOME and KDE before
   calling this done, not just one DE.

**Effort:** Medium — the D-Bus interface itself is small, but this is
Flower's first time exporting (not just consuming) a D-Bus service.
**Risk:** Medium — desktop-environment inconsistency is the main practical
risk, not the D-Bus mechanics themselves.

---

## Phase 4 — Android (`MediaSession`)

**Problem:** Same gap; without a `MediaSession`, Android gives Flower no
notification transport controls, no lock-screen controls, and Bluetooth
headset buttons do nothing.

**Plan:**
1. Implement `IPlatformNowPlaying` backed by AndroidX's `MediaSessionCompat`
   (or `androidx.media3.session.MediaSession` if Flower ends up pulling in
   Media3 for other reasons — check before adding a second media library to
   the Android target). Publish `PlaybackStateCompat`/`MediaMetadataCompat`
   from the same state as the other three platforms.
2. Implement `MediaSessionCompat.Callback.onPlay`/`onPause`/`onSkipToNext`/
   `onSkipToPrevious`, again calling straight into
   `PlaylistControlViewModel`'s existing methods.
3. A `MediaSession` normally pairs with a foreground `Notification` (the
   persistent playback notification most Android music apps show) — decide
   whether Flower needs that notification now or can register the session
   without one initially; a `MediaSession` with no accompanying foreground
   service notification risks Android killing background playback more
   aggressively.

**Effort:** Medium. **Risk:** Medium — same "first real mobile audio
verification" caveat as iOS (`CROSS-PLATFORM-PLAN.md` #4/#7), plus Android's
foreground-service/notification requirements are a genuinely new concern for
Flower, not just new interop.

---

## Suggested execution order

1. **Phase 1** (Windows/SMTC) — cheapest, least risky, most mechanical;
   validates the `IPlatformNowPlaying` seam shape before the harder
   platforms build on it.
2. **Phase 3** (Linux/MPRIS) — no mobile-verification dependency, can
   proceed independently of mobile-build status.
3. **Phase 2** (macOS/iOS) — do macOS's half anytime; gate iOS's half on
   `CROSS-PLATFORM-PLAN.md` #4/#7 (mobile playback verified working at all)
   the same way `AIRPLAY-BLUETOOTH-PLAN.md` Phase 2 already does, and land
   both Apple-platform features' audio-session setup together rather than
   twice.
4. **Phase 4** (Android) — same mobile-verification gate as iOS; also the
   one platform here carrying a genuinely new architectural question
   (foreground notification requirement) rather than pure interop, so worth
   doing last once the pattern is proven on the other three.
