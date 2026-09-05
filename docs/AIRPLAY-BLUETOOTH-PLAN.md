# Bluetooth (all platforms) + AirPlay (macOS/iOS) — Output Routing Plan

Scope: **Bluetooth output** on Windows, macOS, Linux, Android (iOS gets it as a side effect of Phase 2). Phase 1's desktop picker and Phase 2's iOS/macOS route pickers are **built** — see below. **AirPlay sender** on macOS/iOS only — Windows/Linux have no viable AirPlay sender path (no first-party access; third-party reverse-engineered senders like `pyatv`/`shairplay` are stuck on legacy AirPlay 1/RAOP and don't work against AirPlay 2 receivers), dropped from scope by decision.

## Key findings

- Bluetooth and AirPlay (via macOS CoreAudio routing) already work today with zero code as long as the user sets the OS default output manually — this plan adds an in-app picker instead of sending users to System Settings.
- **Historical, and the reason the pipeline has the shape it does:** LibVLC's `RendererDiscoverer` only surfaced Chromecast/UPnP, a dead end for AirPlay, while `MediaPlayer.SetAudioCallbacks`/`SetAudioFormatCallback` let Flower keep LibVLC for decode and own the output stage itself. That seam is what the gapless work was built on, and it outlived the decoder that motivated it — the pipeline is now `FfmpegTrackDecoder` → `GaplessRingBuffer` → `IAudioSink`, same shape, no LibVLC anywhere in it.
- **On iOS the audio route belongs to the `AVAudioSession`, not to a player object.** Every output unit in the process follows it, `MiniaudioSink`'s CoreAudio `RemoteIO` unit included — so `AVRoutePickerView` works there without Flower rendering through `AVAudioEngine`. macOS has no `AVAudioSession` at all, which is why the two halves of Phase 2 came out so differently — and why the macOS half rests on an assumption that still needs confirming by hand.
- `AVRoutePickerView` (AVKit) is Apple's native Bluetooth+AirPlay route picker (same one Music uses) — Phase 2 hangs its UI on this rather than a custom picker.
- Android/Windows need no Phase 2 work: OS-level A2DP pairing already transparently redirects all app audio once Bluetooth is the active output.

## Phase 1 — In-app output-device picker (Windows/macOS/Linux) — **done**

Shipped as a speaker button in the desktop status bar, immediately right of
`VolumeControl`, dropping a flyout of output devices with a tick on the one in
use.

- `IAudioSink` gained `GetOutputDevices()`/`OutputDeviceId`/`SetOutputDevice(id)`, forwarded through `IAudioManager` by `GaplessAudioManager`. `MiniaudioSink` implements them over `ma_context_get_devices`; `WebAudioManager` reports no devices, which is what hides the control in a browser tab.
- Ids are base64 of miniaudio's whole 256-byte `ma_device_id` union — opaque above `IAudioSink`, so nothing upstream has to know a CoreAudio UID from a WASAPI wide string. `MiniaudioBindingLayoutTests` pins the struct layout that encoding and the enumeration walk both assume, since Miniaudio-CS's generated structs have no runtime `sizeof` escape hatch the way `ma_context` does.
- miniaudio has no "move this device to that endpoint" call, so a change uninits the `ma_device` and inits a new one against the chosen id. Consequences handled in `MiniaudioSink`: master volume is tracked in managed state (a fresh device resets it to 1.0), a running device is restarted without emitting Playing/Paused (a swap is not a pause), and a device that vanished between enumeration and click falls back to the system default rather than leaving no output open.
- Re-enumerates when the flyout opens; no hotplug subscription for the *list* (a device appearing does not repopulate an open flyout). Losing the device in use is a separate thing and is now noticed - see "Losing the output device" below. No persistence across relaunches — an explicit pick lasts for the session, and the "System default" row (a distinct state from "the device that happens to be default right now") is where it starts.
- Desktop-only by construction rather than by a platform check: the mobile shell is a separate view tree (`MobileMainView`) that simply never hosts the control. It stays visible on macOS alongside Phase 2's `AVRoutePickerView` — see Phase 2 for why the two are complementary rather than redundant.
- Verified against real hardware: enumeration, switching between three CoreAudio endpoints mid-playback with no underruns, volume preserved across swaps, and a malformed id falling back cleanly. Not yet verified on Windows/Linux, or against a real Bluetooth endpoint appearing and disappearing.

## Phase 2 — real AirPlay + Bluetooth via `AVRoutePickerView` — **done on iOS; macOS built, unverified**

Shipped on iOS as Apple's own route button in the Now Playing sheet's top bar,
plus the session and route-change work behind it, and on macOS as the same
button in the desktop status bar — which needed a new platform head to reach
AVKit at all, see "macOS: the `Flower.MacOS` head" below.

**The phase's original premise was wrong about iOS, in a way that made it much
smaller.** It was written when LibVLC owned the output stage and said
`AVRoutePickerView` "requires audio to actually play through `AVAudioEngine`
rather than another output module". On iOS that is not so: the *route* belongs
to the `AVAudioSession`, not to any player object, and every output unit in the
process follows it — including the CoreAudio `RemoteIO` unit that
`MiniaudioSink`'s `ma_device` ends up on. So iOS needed no new `IAudioSink` at
all, and `AppleAudioEngineSink` was not written. That claim is a macOS one,
where output-device selection genuinely is per-player.

What shipped:

- **`IPlatformRoutePicker`/`PlatformRoutePicker.Current`** (`Flower/Services/`), set by `Flower.iOS`'s `AppDelegate` alongside `PlatformMdns`/`PlatformNowPlaying`/`PlatformAudioSession`. `Flower.iOS`'s `AppleRoutePicker` returns an Avalonia `NativeControlHost` wrapping a real `AVRoutePickerView` (via `Avalonia.iOS`'s `UIViewControlHandle`).
- **`RoutePickerControl`** (`Flower/UserControls/`) hosts whatever that hands back, and collapses itself everywhere `Current` is null — which is every platform but iOS. No ViewModel and no `DataContext`: the native view talks to the audio session directly, and Flower is never told to do anything about the route it picks.
- **Route sharing policy.** `AppleAudioSession` now sets `.playback` + default mode + **`LongFormAudio`**, which is what decides whether the picker offers AirPlay 2 receivers (HomePods, an Apple TV, a multi-room group) or only legacy AirPlay 1 devices. This is the change that actually buys AirPlay 2; the button alone would not.
- **Route-change observer.** `AppleAudioSession` observes `AVAudioSession` route changes and reports the one reason that matters, `OldDeviceUnavailable`, up through a new `IPlatformAudioSession.OutputDeviceLost` — marshalled onto the main thread, since AVFoundation posts on its own. `GaplessAudioManager` pauses in response (through `Pause()`, so the session is released exactly as a tapped pause button would), which is what every music app on the platform does; the alternative is Flower carrying on at full volume through the handset speaker. The deciding is shared and tested (`GaplessAudioManagerTests`); the platform only reports the fact.
- **The category has to be set at launch, not at first play** (found on a device, after the above). `ActivateForPlayback` used to set the category and activate together, so the first time it ran was the first tap of play — long after `GaplessAudioManager`'s constructor started the sink and `MiniaudioSink` built its `ma_device`. An output unit created while the session still holds iOS's process default, `SoloAmbient`, keeps behaving like that category no matter what is set underneath it afterwards: silenced by the ringer switch, so sound reached AirPods but never the phone's own speaker, and killed by the screen locking, `UIBackgroundModes: audio` or not. `AppleAudioSession` now sets the category in its constructor and only *activates* around real playback, which is the half that would interrupt another app.
- **And configuring it must never be able to cost activation.** Setting the category on its own did not fix the device; the second half did. `ActivateForPlayback` used to set the category and, if that call returned an error, return without ever calling `setActive:` - and the call it makes is the fussy four-argument one, which iOS rejects outright if it dislikes any part of the category/mode/policy/options combination. One refused call therefore cost both the category and the activation, which is precisely a session that is inaudible with the ringer switch down and dead at the lock screen. It is now a ladder - long-form audio, then plain playback with A2DP, then the bare category - and activation happens regardless of how far down it got: the route sharing policy is a feature, being in the playback category at all is the app working. Confirmed fixed on a real iPhone; which rung the device actually settles on is not known, because the app's log goes to its own file rather than the `devicectl --console` stream. `LogSessionState` records category, mode, policy, options and the live route at startup and at every activation, so the next question of this kind is answerable from the Log tab.
- **Interruptions are reported and acted on.** `AVAudioSession` interruption notifications (a call, Siri, an alarm, another non-mixing app) come up through `IPlatformAudioSession.PlaybackInterrupted` / `PlaybackInterruptionEnded`, the same shape as `OutputDeviceLost`: the platform reports, `GaplessAudioManager` decides. It pauses on the way in — the sound has already stopped, and without this the transport controls and the Lock Screen card go on claiming a track is playing — and resumes on the way out only when the OS's own `ShouldResume` says it may *and* Flower was playing when the interruption began. `DeactivateAfterPlayback` no-ops while interrupted, since the OS has already taken the session and handing it back a second time only produces a warning per phone call.
- Verified: all ten projects build, and the fast suite passes with five new tests (`RoutePickerControlTests`, three in `GaplessAudioManagerTests`). **Not verified on a device or simulator** — that nothing here has actually been seen routing to an AirPlay speaker is the gap, and it rides on the same real-device verification `STORE-DEPLOYMENT-PLAN.md` Phase 3 is waiting on.

### macOS: the `Flower.MacOS` head

`AVRoutePickerView` needs the `net10.0-macos` TFM's Apple bindings, and
`Flower.Desktop` is plain `net10.0` and has to be — Avalonia's desktop backend
targets it. So macOS got its own head rather than the workaround: `Flower.MacOS`,
`net10.0-macos`, `Avalonia.Desktop` and the rest resolving their `net10.0`
assets unchanged (a platform TFM is a superset for package resolution), with
AVKit and AppKit reachable on top. This is what `PlatformAudioManager`'s own
comment always anticipated ("a future Flower.Mac's Program.cs"); it also stops
being only about one button, since it is the first place on the desktop where
`MPNowPlayingInfoCenter`, real Bonjour, and typed AppKit are available.

- **Needs the `macos` workload**: `sudo dotnet workload install macos`. Without it the whole-solution build fails with NETSDK1147, the same way `Flower.iOS` would.
- **And a matching Xcode, which is the awkward part.** The macOS SDK pins an exact major.minor (26.5.10315 wants Xcode 26.6) and errors out otherwise, where `Flower.iOS` sails through on the same workload version. Neither Xcode on this machine is 26.6, so the build that produced the first working bundle was `DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer dotnet build Flower.MacOS/Flower.MacOS.csproj -p:ValidateXcodeVersion=false` — 26.3, with the SDK's own escape hatch. It built clean and ran, so the pin is stricter than the actual requirement here. Deliberately *not* set in the csproj: it would disable a real check for every future build and every other machine, and this is a local version skew, not a project setting.
- `Flower.Desktop` is unchanged and still runs on macOS — from Rider, from `dotnet run` — just without AVKit. It has not been narrowed to Windows/Linux, and doing that is a separate decision.
- Two files stopped belonging to one head on the way: `MacDockIcon` moved into the shared library (`Flower/Platform/`, still `objc_msgSend` — it has to compile from plain `net10.0` too), and the DOTNET_EnableCrashReport self-relaunch became `Flower.Core`'s `CrashReportRelaunch`, since both desktop heads need it.
- **The route picker sits beside the Phase 1 device picker, not instead of it.** An earlier note here said Phase 1's control "should hide itself on macOS once Phase 2 ships"; that was wrong. They list different things — `OutputDeviceControl` lists the CoreAudio devices miniaudio can render to, `AVRoutePickerView` reaches AirPlay receivers that are not CoreAudio devices — and dropping one loses real reach.
- **It compiled and ran first time.** No AVKit/AppKit binding fixes were needed — the `AVRoutePickerView(CGRect)` ctor, `RoutePickerButtonBordered`, and the `NativeHandle` the `PlatformHandle` is built from all bound as written. `Flower.app` launches, the crash-report self-relaunch produces its expected parent/child pair, and both controls render in the status bar: `OutputDeviceControl`'s speaker and, beside it, Apple's own AirPlay glyph drawn by a real `AVRoutePickerView` inside the `NativeControlHost`. So the hosting half of the macOS bet is settled.
- **The load-bearing claim is still unverified.** macOS has no `AVAudioSession`, so the iOS argument ("the session owns the route, every output unit follows") does not transfer. The macOS assumption is instead that an `AVRoutePickerView` with no `player` attached drives the *system* audio output context, which miniaudio then follows because it renders to the default output device. If that turns out to be false, the picker will open and route nothing, and the fallback is an `AVAudioEngine`-backed `IAudioSink` — now possible in this head, where before it was not. Unlike the iOS half this is cheap to check: run `Flower.MacOS`, click the button, pick a HomePod.

### Losing the output device

Pausing when the output disappears started as an iOS-only behaviour, because
`AVAudioSession` was the only thing that could see it happen. That was a gap
rather than a platform difference: on macOS, Windows and Linux, pulling the
headphones left Flower playing on out loud through whatever the OS fell back
to. The policy was already shared and already tested — `OutputDeviceLost` →
`GaplessAudioManager.Pause()` — only the reporting was missing.

- `IAudioSink` gained its own `OutputDeviceLost`, the twin of
  `IPlatformAudioSession`'s, and `GaplessAudioManager` subscribes both to the
  same handler. Nothing about the decision is written twice, and no platform
  reports through both: iOS's sink cannot see a route move, and no other
  platform has a session.
- `MiniaudioSink` raises it off `ma_device_config.notificationCallback`, so one
  implementation covers macOS, Windows, Linux and Android at once, rather than
  three `IPlatformAudioSession` twins.
- **`rerouted` carries no reason**, which is the whole difficulty. iOS gets
  `OldDeviceUnavailable` and can act on it directly; miniaudio reports only
  that the backend moved us. Unplugging headphones and changing the default
  output in Sound settings arrive identically, and pausing on the second would
  be worse than the bug. So the sink tracks which device it actually opened
  (the explicit pick, or whichever was flagged default at the time) and on a
  reroute re-enumerates: still in the list means the user moved on purpose and
  playback follows them; gone means it was taken away, and Flower pauses.
- An unasked-for `stopped` is unambiguous by comparison, and is handled
  separately: the endpoint is gone outright, so an explicit pick that no longer
  enumerates is dropped back to "System default", a fresh device is opened so a
  later resume has somewhere to go, and *then* the pause is reported.
  Intentional stops are told apart by a depth counter, since miniaudio raises
  the same notification synchronously from inside `device_stop`.
- **Unverified against real hardware.** The unit tests cover the shared
  decision from both reporters; nothing has yet watched a real Bluetooth
  speaker switch off. That is the same hand-verification Phase 1 still owes,
  and it now has a second thing to check while the headphones are out.

Effort for macOS: was Large, came out Medium — the head itself is small, and no
new audio path was needed. Risk: concentrated entirely in the unverified claim
above.

## Phase 3 — Android: verify, don't build

No work expected. Once a Bluetooth device is the active output, Android routes all app audio through it automatically. Confirm with a smoke test once a device is available (rides on `STORE-DEPLOYMENT-PLAN.md` Phase 3's real-device verification). Effort/Risk: None.

Neither mobile platform gets Phase 1's picker, and that is the intended end
state rather than a gap: iOS and Android both hand the user a system route
picker (Control Centre / the output tile) that every app's audio follows, so an
in-app list would duplicate the OS one and be the worse of the two — it cannot
show or drive AirPlay at all. Phase 2's `AVRoutePickerView` is not a picker
Flower builds either; it is Apple's own control, surfaced in-app.

## Suggested order

1. ~~Phase 1~~ — done on desktop; still owes a Windows/Linux pass and a real Bluetooth endpoint, which is now also what would verify "Losing the output device".
2. ~~Phase 2~~ — done on iOS and macOS. macOS now builds, runs, and draws the button; what remains there is one click on a real AirPlay receiver to learn whether it routes anything. iOS needs a real phone.
3. Phase 3 (trivial), together with a device pass over Phase 2 — both need the same thing, which is a real phone.
