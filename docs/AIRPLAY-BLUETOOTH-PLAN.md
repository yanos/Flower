# Bluetooth (all platforms) + AirPlay (macOS/iOS) — Output Routing Plan

Source: investigation into adding audio-output routing to Flower, prompted by
"can we send audio to AirPlay/Bluetooth devices." Scope, per that
investigation's conclusions: **Bluetooth output on Windows, macOS, Linux,
Android** (iOS gets it as a side effect of Phase 2, not a separate item) plus
**AirPlay sender support on macOS and iOS only** — Windows/Linux have no
viable AirPlay sender path (no first-party protocol access, and the only
third-party reverse-engineered senders — `pyatv`, `shairplay`/`libraop` — are
scoped to the legacy AirPlay 1/RAOP protocol and explicitly don't work
against HomePod or modern AirPlay 2 receivers; not a foundation worth
building on) and were dropped from scope by decision, not just left for later.

## Research summary (why this is shaped the way it is)

- Today, `VlcAudioManager` (`Flower/Manager/VlcAudioManager.cs`) is one
  `IAudioManager` implementation shared by every platform, built on a plain
  LibVLCSharp `MediaPlayer` with no output-device selection at all — it plays
  through whatever the OS's current default audio output is. That means
  Bluetooth *and* AirPlay (via macOS's own CoreAudio routing) already work
  today with zero code, as long as the user manually sets the OS's default
  output device — this plan is about giving Flower an in-app picker instead
  of sending users to System Settings.
- LibVLC's own renderer-discovery API (`libvlc_renderer_discoverer_*`,
  exposed as `RendererDiscoverer` in LibVLCSharp) only surfaces
  Chromecast/UPnP renderers — confirmed dead end for AirPlay, not
  investigated further.
- The key finding that shapes Phase 2 below: **LibVLCSharp 3.10.0 (the
  version Flower references on every target, confirmed via
  `Flower/Flower.csproj`, `Flower.Desktop`, `Flower.iOS`, `Flower.Android`)
  already exposes `MediaPlayer.SetAudioCallbacks`/`SetAudioFormatCallback`
  as managed API** — no P/Invoke needed. This lets Flower keep LibVLC for
  everything it's good at (decoding, seeking, format support, metadata) and
  redirect only the final output stage on Apple platforms into `AVAudioEngine`,
  rather than dropping LibVLC there entirely. Confirmed present and identical
  in both the `net8.0` desktop DLL and the `net10.0-android` DLL, so nothing
  about the API itself is Apple-specific — only how Phase 2 chooses to use it.
- `AVRoutePickerView` (AVKit, macOS 10.15+/iOS 11+) is Apple's own system
  route-picker widget (the same one Music/Podcasts use) and lists both
  Bluetooth outputs and AirPlay/AirPlay 2 receivers in one native control —
  this is what Phase 2 hangs its UI on, rather than building a custom picker.
- Android and Windows don't need any of the Phase 2 machinery for Bluetooth:
  OS-level A2DP pairing already transparently redirects all app audio
  (LibVLC included) once a Bluetooth device is the active system output,
  same mechanism already covering AirPlay-via-macOS today.

---

## Phase 1 — In-app Bluetooth/output-device picker (Windows, macOS, Linux)

**Problem:** Bluetooth speakers/headphones already work via OS-level default
output selection, but there's no in-app way to see or switch between output
devices without leaving Flower for System Settings.

**Plan:**
1. Extend `IAudioManager` (`Flower/Manager/IAudioManager.cs`) with:
   - `IReadOnlyList<AudioOutputDevice> GetOutputDevices()` — id + display name.
   - `void SetOutputDevice(string? deviceId)` — `null` meaning "system default."
2. Implement both in `VlcAudioManager` using the existing (confirmed present)
   `MediaPlayer.AudioOutputDeviceEnum`/`AudioOutputDeviceSet` LibVLCSharp API
   — no native interop needed, works identically on Windows/macOS/Linux.
3. Add a small output-picker affordance next to `VolumeControl` in the top
   bar (a speaker icon opening a flyout listing devices, same interaction
   shape as the existing column header context menus). Re-enumerate on open
   rather than subscribing to hotplug events — simplest option, matches how
   infrequently output devices change mid-session.
4. No persistence across relaunches for v1 (always starts on system
   default) — add later only if this turns out to matter in practice.
5. Desktop-only, gated the same way `OpenDatabaseLocationCommand`/`NativeMenu`
   already are (`IClassicDesktopStyleApplicationLifetime`) — on macOS this
   picker is superseded by Phase 2's native `AVRoutePickerView` once that
   ships (see Phase 2 step 4), so it should hide itself there rather than
   showing two competing pickers side by side.

**Effort:** Small–Medium. **Risk:** Low — purely additive; today's
default-device playback path is untouched as the fallback.

---

## Phase 2 — macOS + iOS: real AirPlay + Bluetooth via `AVAudioEngine`

**Problem:** Getting Apple's own unified Bluetooth+AirPlay picker
(`AVRoutePickerView`) requires audio to actually be playing through
`AVAudioEngine`, not LibVLC's own output module. Phase 1's device-enum API
almost certainly won't surface AirPlay receivers as pickable "devices" the
way `AVRoutePickerView` does, and doesn't exist as a device-picker concept on
iOS at all.

**Plan:**
1. Introduce an output-sink seam so `VlcAudioManager` doesn't need a full
   fork: keep it for decode/seek/metadata everywhere, but on macOS/iOS hook
   `MediaPlayer.SetAudioFormatCallback` (to learn/negotiate sample
   rate+channel layout) and `SetAudioCallbacks` (play/pause/resume/flush/drain
   — play callback hands over `(IntPtr data, IntPtr samples, uint count, long
   pts)`, i.e. a raw PCM buffer, frame count, and timestamp).
2. Feed those PCM frames into a small ring buffer that an `AVAudioSourceNode`
   in an `AVAudioEngine` graph drains from its (pull-based) render block.
   **This push→pull bridge is the single riskiest piece of the whole plan** —
   LibVLC calls the play callback as data becomes available; `AVAudioEngine`
   pulls at its own render cadence. Needs a real buffering/underrun strategy,
   not just a naive handoff.
3. Add an `AVRoutePickerView` instance (AVKit), hosted via Avalonia's native
   view interop (`NativeControlHost`) as a button in the top bar, shown
   *only* on macOS/iOS (same platform-branch-at-the-presentation-layer
   pattern `CROSS-PLATFORM-PLAN.md` #1 already uses for `TrackInfoWindow`).
   Tapping it shows Apple's system menu of Bluetooth + AirPlay targets; the
   OS owns route switching, auth, and reconnection from there.
4. On macOS this button replaces Phase 1's generic output picker (no reason
   to keep both); on iOS there's no Phase 1 picker to begin with, so this is
   iOS's only output-routing UI.
5. iOS additionally needs an `AVAudioSession` configured for `.playback`
   category (background audio) and a `routeChangeNotification` handler
   (headphone-unplug-style pause behavior) — currently nonexistent per
   `MOBILE-PLAN.md`'s finding that no mobile audio-session code exists at all.
6. **Open design question to resolve before implementing, not during:** once
   LibVLC's output is bypassed via `SetAudioCallbacks`, does
   `MediaPlayer.Volume` still do anything, or does Flower need to move volume
   scaling into the callback (or onto `AVAudioEngine`'s mixer node) itself?
   `PlaylistControlViewModel`/`CurrentlyPlayingControlViewModel` both assume
   `IAudioManager.Volume`/`Position`/`Time` behave the same regardless of
   output path — verify this holds before wiring the UI up, not after.

**Effort:** Large — the biggest item here, and the first time `IAudioManager`
would need a real per-platform fork rather than one shared implementation.
**Risk:** Medium–High (real-time audio bridging is the main unknown;
route/volume/seek behavior parity is the second).

---

## Phase 3 — Android: verify, don't build

**Problem:** none, expected — included only so this isn't silently dropped.

**Plan:** Once a paired Bluetooth device is the active system output, Android
routes all app audio (LibVLC included) through it automatically, same
mechanism as desktop's baseline. No AirPlay scope on Android (Apple-only
protocol, out of scope per this plan's opening decision). Confirm with a
smoke test once a real device/emulator is available — this rides on
`CROSS-PLATFORM-PLAN.md` #4/#7's existing mobile-verification gaps, not new
work of its own.

**Effort:** None (verification only). **Risk:** None.

---

## Suggested execution order

1. **Phase 1** (desktop Bluetooth/output picker) — cheap, immediate value,
   no architectural risk, ships first and validates the UI shape Phase 2
   will partly reuse/replace on macOS.
2. **Phase 3** (Android verify) — trivial, do whenever a device/emulator is
   available; not blocking anything else.
3. **Phase 2** (Apple `AVAudioEngine` + `AVRoutePickerView`) — biggest and
   riskiest, do last. Also depends on `CROSS-PLATFORM-PLAN.md` #4/#7 (LibVLC
   mobile bootstrap verified, Android/iOS actually building and playing
   audio at all) being resolved first — no point adding AirPlay routing on
   top of iOS playback that's never been confirmed working end-to-end.
