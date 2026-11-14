# Media Key / OS "Now Playing" Integration Plan

Hardware media-key (play/pause/next/previous) support + OS "Now Playing"/lock-screen integration, across all five platforms. **iOS, macOS, Windows and Linux are built (November 2026); Android is not started.** iOS has been used on a real phone, and macOS has been tested by hand; Windows and Linux still need manual verification (see "What is left"). Each head sets an `IPlatformNowPlaying` (the seam below), which `NowPlayingIntegrationService` drives from `PlaylistControlViewModel` and `IAudioManager`:

| Head | Implementation | Where |
|---|---|---|
| Flower.iOS, Flower.MacOS | `AppleNowPlaying` — `MPNowPlayingInfoCenter`/`MPRemoteCommandCenter` | `Flower.iOS/AppleNowPlaying.cs`, linked into `Flower.MacOS` |
| Flower.Desktop on Windows | `WindowsNowPlaying` over `WindowsMediaTransportControls` | `Flower/Platform/` |
| Flower.Desktop on Linux | `MprisNowPlaying` — an MPRIS player on the session bus | `Flower/Platform/` |

## What is left

1. **Try Windows and Linux by hand.** Windows: the media keys and the flyout above the volume overlay, its art, and the lock screen. Linux: GNOME's media widget in the clock menu and KDE's in the tray (`playerctl status`/`playerctl metadata` show what they read).
2. **Windows' vtable slots are proved only by CI.** `WindowsMediaTransportControlsTests` (in `Flower.Tests.Windows`) publishes a song to a hidden window's controls and reads it back; it has not yet run, since nothing here builds on Windows outside CI.
3. **Android** — below, with `STORE-DEPLOYMENT-PLAN.md` Phase 4's foreground service.
4. **Seeking from the OS.** The seam carries play/pause, next and previous only. MPRIS says `CanSeek` false, Windows' timeline shows progress without accepting a drag, and Apple's card has no scrubber. A `Seek(TimeSpan)` command on the seam would light all three up.

## As built on the desktops

- **macOS shares iOS's file.** MediaPlayer is the same framework on both; only the artwork's image type differs (`#if __MACOS__`). macOS also needs `MPNowPlayingInfoCenter.PlaybackState` set outright: it has no audio session to infer playing from, and without it the card shows but the play key goes to the app it last went to.
- **Windows is raw WinRT, not the projection.** `Flower.Desktop` is one plain `net10.0` build for Windows and Linux, and C#/WinRT needs a `net10.0-windows10.0.x` target, so `WindowsMediaTransportControls` calls the interfaces through their vtables: `ISystemMediaTransportControlsInterop.GetForWindow` for the main window, the display updater's music properties, a thumbnail built from bytes (`SHCreateMemStream` → `CreateRandomAccessStreamOverStream` → `RandomAccessStreamReference.CreateFromStream`), the timeline, and a hand-made agile `TypedEventHandler` for `ButtonPressed` (IID computed from its parameterized signature). The slot numbers come from Wine's `windows.media.idl`, which follows the SDK's declaration order. `WindowsNowPlaying` opens the controls on the first song, once the main window exists, and does everything on the UI thread; it restates the timeline only when the state changes or every 5 s, since the flyout moves its bar along by itself.
- **Linux is hand-written over `Tmds.DBus.Protocol`**, the version Avalonia's FreeDesktop backend already ships, now referenced by `Flower` directly. The bus name is `org.mpris.MediaPlayer2.flower`, or `….instance<pid>` when another Flower holds it. Art goes to a file named by its content under `$XDG_RUNTIME_DIR/flower-now-playing/`, since MPRIS takes a URL; the previous one is deleted when the song changes. Position is answered on request and a jump is announced with `Seeked`, never in `PropertiesChanged`, as the spec asks. No session bus (a console login) means no player and nothing else changes. `MprisNowPlayingTests` drives it from a second connection on a private `dbus-daemon` (`PrivateSessionBus`), so it runs in CI's Linux job and on a Mac with MacPorts' or Homebrew's D-Bus.
- **Play, Pause and Stop map onto one toggle** on Windows and Linux, which have them as separate commands: each acts only when it would change the state the player last published, so a Pause pressed while paused never starts the music.

## Why one plan, not per-platform

No cross-platform media-key API exists — each OS routes hardware/lock-screen/headset-button commands through its own Now Playing service, and registering with that service (to publish metadata/play state) is also how you receive commands back. So "media keys" and "now-playing integration" are the same feature on every platform, not two, and mobile is in scope too, not just desktop.

| Platform | Mechanism |
|---|---|
| macOS | `MPRemoteCommandCenter`/`MPNowPlayingInfoCenter` (MediaPlayer framework) — the old raw-NSEvent key trick is unreliable, not worth building on |
| iOS | Same API family as macOS — one shared Apple implementation covers both |
| Windows | `SystemMediaTransportControls` (WinRT), called through its vtables — see "As built on the desktops" for why not the projection |
| Linux | MPRIS over D-Bus — the DE owns key capture and forwards to the registered player. Built on the `Tmds.DBus.Protocol` that `Avalonia.FreeDesktop` already brings |
| Android | `MediaSession` (AndroidX) — also what makes Bluetooth headset buttons and Android Auto work |

## Plan

Add an `IPlatformNowPlaying` seam (a per-platform service behind one interface, with the caller hiding the affordance where the platform returns nothing — the shape the September 2026 review's E7 proposed for `Process.Start`, which was built as a static `PlatformShell` instead because only the two desktop heads had anything to register; see `CITED-DECISIONS.md`). As built, the head sets it as `PlatformNowPlaying.Current` before Avalonia starts, the same convention as `PlatformMdns.Current`, and `NowPlayingIntegrationService` does nothing where it is left null. Each implementation publishes metadata/play state from `PlaylistControlViewModel`/`Track` and wires transport commands straight into `PlaylistControlViewModel.PlayOrPause()`/`Next()`/`Previous()` — no changes needed to that class.

- **Windows** — built; see "As built on the desktops".
- **macOS/iOS** — built, one shared `AppleNowPlaying`.
- **Linux** — built, `MprisNowPlaying`; still to smoke-test against both GNOME and KDE, whose MPRIS handling isn't perfectly consistent.
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

Windows, Linux, macOS and iOS are built. What remains is the hand checks in "What is left", then Android, alongside `STORE-DEPLOYMENT-PLAN.md` Phase 4's foreground service, since the media session and the notification are one piece of work there.
