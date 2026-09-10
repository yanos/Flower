# Media Key / OS "Now Playing" Integration Plan

Hardware media-key (play/pause/next/previous) support + OS "Now Playing"/lock-screen integration, across all five platforms. No code for this exists anywhere in Flower today. Not yet started.

## Why one plan, not per-platform

No cross-platform media-key API exists — each OS routes hardware/lock-screen/headset-button commands through its own Now Playing service, and registering with that service (to publish metadata/play state) is also how you receive commands back. So "media keys" and "now-playing integration" are the same feature on every platform, not two, and mobile is in scope too, not just desktop.

| Platform | Mechanism |
|---|---|
| macOS | `MPRemoteCommandCenter`/`MPNowPlayingInfoCenter` (MediaPlayer framework) — the old raw-NSEvent key trick is unreliable, not worth building on |
| iOS | Same API family as macOS — one shared Apple implementation covers both |
| Windows | `SystemMediaTransportControls` (WinRT) — first-class .NET projection |
| Linux | MPRIS over D-Bus — the DE owns key capture and forwards to the registered player. Avalonia already transitively pulls in `Tmds.DBus.Protocol` via `Avalonia.FreeDesktop`; reuse that rather than adding a new dependency |
| Android | `MediaSession` (AndroidX) — also what makes Bluetooth headset buttons and Android Auto work |

## Plan

Add an `IPlatformNowPlaying` seam (the same shape as the `IPlatformShell` proposed in `CODE-REVIEW-2026-09.md` E7 — a per-platform service behind one interface, with the caller hiding the affordance where the platform returns nothing), registered per-platform in `App.axaml.cs`'s DI container. Each implementation publishes metadata/play state from `PlaylistControlViewModel`/`Track` and wires transport commands straight into `PlaylistControlViewModel.PlayOrPause()`/`Next()`/`Previous()` — no changes needed to that class.

- **Windows** — wrap `SystemMediaTransportControls`, set `DisplayUpdater` fields, update `PlaybackStatus`, verify taskbar thumbnail controls and the lock-screen card. Small-Medium effort, Low risk.
- **macOS/iOS** — shared `MPNowPlayingInfoCenter`/`MPRemoteCommandCenter` implementation; land alongside whatever native interop `AIRPLAY-BLUETOOTH-PLAN.md` Phase 2 introduces rather than as a separate effort. iOS also needs the `audio` `UIBackgroundModes` declaration (no mobile audio-session code exists yet) — configure the audio session once, not twice, alongside AirPlay/Bluetooth work. Medium effort; Low risk on macOS, iOS carries the same "first real mobile audio verification" risk flagged elsewhere.
- **Linux** — export `org.mpris.MediaPlayer2`/`.Player` as a D-Bus service under `org.mpris.MediaPlayer2.Flower`; confirm whether the transitive `Tmds.DBus` reference is sufficient or an explicit package reference is needed for exporting (vs. consuming) a service. Smoke-test against both GNOME and KDE — their MPRIS property/signal handling isn't perfectly consistent. Medium effort, Medium risk (DE inconsistency, not the D-Bus mechanics).
- **Android** — `MediaSessionCompat` (check whether Media3's `MediaSession` makes more sense before adding a second media library), implementing the `onPlay`/`onPause`/`onSkipToNext/Previous` callbacks. Decide whether a foreground notification is needed alongside the session, since Android is more aggressive about killing background playback without one. Medium effort, Medium risk.

## How long the card stays up

The card is not only raised, it has to *survive*, and on iOS what keeps it alive
is the app's `AVAudioSession` being active — deactivating it is how a player
tells the OS it is no longer the now-playing app. Flower used to hand the
session back on every pause (`GaplessAudioManager.Pause` →
`AppleAudioSession.DeactivateAfterPlayback`), which took the Lock Screen card
down the instant it was used to pause. It doesn't any more: only `Stop()`
releases the session, and nothing in the app calls it. The cost is the one
courtesy that deactivation bought — telling whatever Flower interrupted that it
may resume — which is not worth the card, and is not something Apple's own Music
app does either.

The other half is the end of the queue. Auto-advance no longer wraps round to
the first track (see `PlaylistControlViewModel.GetUpcomingEntry`), so an album
genuinely ends; `GaplessAudioManager` notices the queue has played out once the
tail left in the ring has been rendered, puts its output down with a *pause*
rather than a stop, and raises `IAudioManager.PlayedOut`. The ViewModel parks
the queue back at its first track and leaves it there as the current track, so
the card stays up showing it and the album can be started again from the card
without opening the app.

## Suggested order

1. Windows — cheapest, validates the `IPlatformNowPlaying` seam shape.
2. Linux — no mobile-verification dependency.
3. macOS/iOS — macOS anytime; gate iOS's half on mobile playback being verified working, same as `AIRPLAY-BLUETOOTH-PLAN.md` Phase 2.
4. Android — same mobile-verification gate, plus the new foreground-notification question, so do it last once the pattern is proven elsewhere.
