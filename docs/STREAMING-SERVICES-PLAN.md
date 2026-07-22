# Streaming Services (Spotify, Bandcamp, Apple Music) — Integration Plan

Investigation into streaming Spotify/Apple Music/Bandcamp from Flower given valid credentials.

**Bottom line: credentials aren't the gating factor — DRM is.** `IAudioManager.Play(Track)` hands `track.Path`/a URL straight to LibVLC, which decodes anything it's given (this is also how `SYNC-PLAN.md`'s OpenSubsonic streaming works). So the deciding question per service is: **can we get a raw, DRM-free, LibVLC-decodable stream URL?**

| Service | Metadata API | Decodable stream URL? | Playback path | Platforms | Legitimacy |
|---|---|---|---|---|---|
| **Bandcamp** | No official API (scrape) | **Yes** — public `mp3-128` URLs | Existing LibVLC | All 5 | Grey (scraping, no DRM) |
| **Spotify** | Yes (Web API, OAuth2) | **No** — DRM, official SDKs only | Separate player / `librespot` sidecar | Varies | Red (ToS-violating for audio) |
| **Apple Music** | Yes (Apple Music API) | **No** — FairPlay DRM, MusicKit only | Separate native player | macOS/iOS only | Green, Apple-only |

Not yet started; not yet committed to git.

---

## Bandcamp — recommended first

No public API; every third-party client scrapes. A track/album page embeds a `data-tralbum` JSON blob with a direct, signed but public `mp3-128` MP3 URL — playable by LibVLC with no decryption. Purchased (DRM-free, higher quality) downloads require scraping an authenticated fan-collection page.

**Approach:** `BandcampProvider : IMusicProvider`, plain `HttpClient` scraping (search page or the internal `fuzzysearch` autocomplete endpoint) — no third-party library needed. Resolve the stream URL lazily *at play time* (never cache in `library.json` — it's time-limited), hand it to the existing `VlcAudioManager` unchanged. Optional: import a logged-in user's purchased collection as placeholder tracks (`Path == null`), same shape as a sync peer's tracks.

**Limitations:** scraping is brittle and against ToS (though no DRM circumvention); streaming capped at 128kbps; some tracks are purchase-only.

---

## Spotify — legitimate metadata, but no legitimate in-app audio

Web API (OAuth2 PKCE) gives full metadata/search/playlists/Spotify Connect device control, but **returns no audio**. 30-second previews are unreliable (`preview_url` often `null` since late 2024). Web Playback SDK is browser/EME-only. App Remote SDK just remote-controls an already-installed Spotify app. The official C SDK (`libspotify`) is discontinued.

The only way to get decodable audio is **`librespot`** (open-source reimplementation of the Spotify client) — which works, but is a reverse-engineered client that **violates Spotify's Developer Terms**. Ship-ability, not difficulty, is the blocker.

**Recommended:** ship metadata/browse via the Web API + hand playback to an official Spotify client via Spotify Connect (control, not embed) — legitimate and ToS-clean. Document `librespot` as the only technically-possible audio-in-app path but recommend against shipping it.

---

## Apple Music — legitimate, but macOS/iOS-only playback

Apple Music API (REST, cross-platform) covers metadata/search/library with a Developer Token (JWT, requires paid $99/yr Apple Developer Program membership) + a per-user Music User Token (requires an Apple Music subscription). Playback is **MusicKit-only, and MusicKit is Apple-only** — no Windows/Linux playback exists at all, and no raw stream URL exists on any platform (FairPlay DRM).

**Approach:** `AppleMusicProvider : IMusicProvider` for metadata on all 5 platforms. Playback only on macOS/iOS via a native MusicKit player wrapped as a second `IAudioManager` (`MusicKitAudioManager`); Windows/Linux/Android browse-only (greyed out, like an un-downloaded sync placeholder). On macOS, an alternative is AppleScript-controlling Music.app (same technique `ITunesPlayCountImporter` already uses) instead of embedding MusicKit.

This is additive to `SYNC-PLAN.md`'s "iOS owns its files, no `MPMediaLibrary` integration" decision (that was about the local importer, not streaming) — keep the two clearly separate in the UI.

---

## Core architectural work

**Seam 1 — `IMusicProvider` (cheap, shared by all three):** a provider abstraction generalizing what `OpenSubsonicClient` already does — `SearchAsync`, `BrowseLibraryAsync`, `ResolvePlayableUrlAsync(track)` (`null` ⇒ not URL-playable). Add a `Source`/provider tag to `Track` alongside the existing `OriginDeviceFingerprint` pattern. `ResolvePlayableUrlAsync` results must never be persisted to `library.json` (signed/time-limited URLs). Local, sync, and Bandcamp all implement this fully and reuse `VlcAudioManager` untouched.

**Seam 2 — multiple `IAudioManager`s + a router (expensive, DRM services only):** for Spotify/Apple Music, `ResolvePlayableUrlAsync` returns `null` and playback needs a per-backend `IAudioManager` (`MusicKitAudioManager`, `LibrespotAudioManager`) behind an `AudioManagerRouter` that picks the engine from `track.Source`. Hard problems: reconciling two engines' clocks, gapless handoff across engines, volume normalization, running a native SDK player alongside LibVLC. This is why DRM-service playback is a late phase, not a quick win.

**Credentials:** store via OS secret stores (Keychain/DPAPI/libsecret/mobile keystores), never `settings.json` — new `ISecretStore` abstraction. Desktop OAuth redirects via a loopback listener (Flower already runs one for sync, `SyncHttpServer`) or a custom URI scheme.

---

## Recommended sequencing

1. `IMusicProvider` seam + `ResolvePlayableUrlAsync`, refactoring local + OpenSubsonic sync onto it first (no behavior change).
2. Bandcamp public streaming — first real provider, no credentials, no DRM.
3. Bandcamp fan-collection browse + download-to-local.
4. Spotify metadata + Spotify Connect control (no second engine yet).
5. Apple Music metadata (all platforms) + MusicKit playback (macOS/iOS) — introduces Seam 2.
6. *(Optional, not recommended)* Spotify audio-in-app via `librespot` — ToS-violating, documented for completeness only.
