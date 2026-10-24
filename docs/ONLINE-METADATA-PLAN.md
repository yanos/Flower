# Online Metadata Plan

Lyrics, a picture of each artist, and which songs on an album are the popular
ones — looked up on the internet for a library that does not carry them. Not
started. What exists today is the neighbour this builds beside:
`Flower/Services/OnlineCoverArt.cs` (Track Info's Find Online, keyless iTunes,
MusicBrainz and Bandcamp searches), and `Track.Lyrics`, read from the file's
own tag by `Importer` and edited in Track Info's Lyrics tab.

## The rule that shapes it: wherever the library lives

A device does not need a server. A desktop with its own folders and nothing
paired is a whole Flower, and it gets all three features on its own — so none of
this can be a server feature that clients consume. Equally, a paired device
should not be the one calling out to third parties when a server is there to do
it once for everybody. So:

**The lookup engine is one piece of code in `Flower.Core`, and it runs on
whichever host owns the song.**

- **A device on its own** runs the engine in-process and caches in its own
  `flower.db`.
- **A paired device** asks its server, which runs the same engine and caches in
  its own DB. A song that is only on this device and that the server does not
  know (a guest's own file) goes to the local engine, the same as on a device
  on its own.
- **The browser tab** always asks the server. It has a server anyway, and it
  could not do otherwise: Deezer's API sends no `Access-Control-Allow-Origin`
  (checked October 2026), so a WASM page cannot call it.

On the client, this is a seam with two implementations, chosen per song the way
`ISettingsBackend` is chosen per backend: `LocalOnlineMetadata` (the engine,
in-process) and `ServerOnlineMetadata` (signed calls to the paired server).
Views and view models only see the seam and never know which one answered.

Why paired devices go through the server instead of each one calling out:

- **One lookup per library, not one per device.** Five phones opening the same
  artist page make one Deezer request, not five, and MusicBrainz's one request
  a second is one host's budget.
- **Listener privacy.** A friend's phone does not tell Deezer or LRCLIB what
  that friend is playing. Only the server's address is ever seen, and that
  belongs to the owner, who chose this.
- **The owner's switch covers everyone.** If the server has online lookups
  off, no device paired to it looks anything up behind the owner's back.
- **Keys, if any are ever added** (Fanart.tv, Last.fm), sit in one server
  config file, not on each listener's phone. That is the trade `CLAUDE.md`
  says to take.

Each phase below ships both paths at once. Nothing lands local-only first, since
that is how a "temporary" second design is born.

## Sources

Chosen like `OnlineCoverArt`'s: **no key and no account**, so a fresh install
works and nobody, owner or listener, is asked to sign up for anything. Every
claim below was checked live in October 2026.

### Lyrics: LRCLIB

`GET https://lrclib.net/api/get?track_name=&artist_name=&album_name=&duration=`
returns `plainLyrics`, and `syncedLyrics` (LRC, timestamped by line) when
someone has timed it, plus `instrumental: true` for songs with no words. The
duration is part of the match and filters out live and remixed versions with
the same name. It is open and crowd-sourced, needs no key (it asks for a
descriptive `User-Agent`, which `OnlineCoverArt.UserAgent` already is), sends
`Access-Control-Allow-Origin: *`, and publishes its whole database as a dump.
When `/get` finds nothing, `/api/search?q=` is the looser fallback.

Rejected:
- **Genius**: its API returns a link to the lyrics page, not the lyrics.
  Getting the words means scraping, which its terms forbid.
- **Musixmatch**: the free tier returns about 30% of each song's lyrics.
- **lyrics.ovh and similar**: unofficial scrapers that break whenever the
  scraped site changes.

Lyrics are copyrighted, and LRCLIB's legal footing is the same grey every
open-source player that uses it stands on. For one owner and a handful of
listeners that is an acceptable place to be, but it is a reason never to
redistribute the cache.

### Artist pictures: Deezer

`GET https://api.deezer.com/search/artist?q=<name>` returns `picture_xl`, a
1000×1000 JPEG on Deezer's CDN. No key. Navidrome moved to it for artist images
for the same reasons. Coverage is a store's: wide for anything distributed, thin
for a self-released record. Those artists keep the cover collage they get today
(`ArtistHeaderInfo`), which is a fine fallback, not a failure.

Rejected for now:
- **Last.fm**: has served a placeholder star instead of artist images since
  2019.
- **Spotify**: needs an OAuth app, and its developer terms and recent
  Development Mode restrictions make it a poor fit.
- **TheAudioDB**: its free key is for testing only, and production use needs a
  paid key.

Later, if Deezer's misses turn out to matter: **Fanart.tv** (free personal key,
lookups by MusicBrainz artist ID, the best photos, backgrounds and logos) as an
optional server-config key, and **Wikidata/Wikimedia Commons** through the
artist's MusicBrainz relationships (keyless, freely licensed, patchy beyond
well-known artists).

### Popular songs: ListenBrainz first, Deezer second

**ListenBrainz**:
`GET https://api.listenbrainz.org/1/popularity/top-recordings-for-artist/<artist MBID>`
returns the artist's recordings ranked by real listen counts, each with its
`release_name`. One call covers every album the artist has, and the artist
page's "Popular" list comes from the same answer. The data is open (CC0), there
is no key, and it sends `Access-Control-Allow-Origin: *`. It needs the artist's
MusicBrainz ID, which comes from a keyless MusicBrainz artist search
(`OnlineCoverArt` already talks to MusicBrainz) or straight from the file when
it was tagged with Picard. TagLib# exposes `MusicBrainzArtistId`, which
`Importer` does not read yet. Not `/1/metadata/lookup`: that started requiring
an `Authorization` token.

**Deezer** for artists ListenBrainz has too few listens for:
`GET https://api.deezer.com/album/<id>/tracks` gives every track a `rank`, so
one call per album answers it, and the album id comes from
`/search/album?q=artist:"…" album:"…"`.

The two disagree, and that is why ListenBrainz goes first. On OK Computer,
Deezer ranks Let Down first (928,986) and Karma Police third (898,957), while
ListenBrainz puts Karma Police at 3.89M listens and Let Down outside Radiohead's
top eight. Deezer's `rank` is an undocumented score. ListenBrainz's is a count
of plays.

**Do not match by ISRC.** `Track.ISRC` looks like the exact key that removes
name matching, but one recording has many ISRCs (a reissue gets its own; Deezer
files Karma Police under a German code), and Deezer's
`/track/isrc:<code>` answered a known code with an unrelated song.

## Matching

All three sources are searched by name, so they can return the wrong song,
album or artist, and matching is where the real work is. It is pure logic and
gets the tests:

- **Names** are normalised the way `OnlineCoverArt.Rank` already normalises
  them (case, punctuation, "The", bracketed "Remastered 2011"-style suffixes).
  That code moves to `Flower.Core` and is shared, not copied.
- **Songs** match within an album by normalised title plus duration within a
  few seconds, which tells a song from its live version and an intro from its
  track.
- **Albums** among Deezer's results are picked by normalised title, then by
  track count and total duration against the library's album.
- **Artists** with a shared name are told apart by asking the search
  for one whose albums overlap the library's albums by that artist. An artist
  picture of the wrong person is the most visible mistake this feature can
  make.

Nothing found by name is written into a file automatically, for the reason
`OnlineCoverArt` gives: a name match is sometimes wrong, and a file is the
record every other device copies.

## Where results live

| | Fetched | Stored | Reaches a paired device |
|---|---|---|---|
| Lyrics | When a song with no Lyrics tag has its lyrics opened | Cache table keyed by track id, with the source and whether it is synced | `GET /api/flower/v1/lyrics?track=<id>`, cached on the device after |
| Artist picture | When an artist page or row is drawn | Image cache keyed by normalised artist name, beside the album-art cache | `GET /api/flower/v1/artist-image?artist=<name>` on the **art** budget, cached on the device like a cover |
| Popularity | When an album or artist page opens, refreshed after 30 days | A score per track id, plus when it was checked | Rides in the `/library` catalog as a track field, so an album page knows its popular songs offline |

**Lyrics found online are shown, not saved.** The Lyrics tab shows them marked
with their source ("from LRCLIB"). *Keep* turns them into an ordinary Lyrics
tag edit through `TrackTags`, which carries the existing rules with it
(`SyncRolePolicy.MayEditSong`, `/tags`, newest edit wins). A song whose tag
already has lyrics is never looked up: the file is the authority.

**Popularity in the catalog** means a refresh changes catalog rows. That is
acceptable because it happens at most monthly per album, but it is exactly the
"sync rewrites the library" shape `CODE-REVIEW-2026-10.md` warns about, so a
refresh that returns the same ranking must not count as a change.

**A miss is cached too** (for 30 days), so an artist Deezer does not know is
not searched again on every scroll.

## Settings

One switch: "Look up lyrics, artist pictures and popular songs online", on by
default.
- On a device on its own: `AppSettings`.
- On the server: `FlowerServerOptions`, which also governs every device paired
  to it (see above). The paired device's own settings show the server's answer
  rather than a switch of their own, using a `SettingsCapabilities` flag like
  the other per-backend differences.

## Phases

Each phase lands in both topologies (local engine and server route) together.

1. **Engine skeleton and artist pictures.** `Flower.Core` lookup engine with
   the shared HTTP client, `User-Agent`, normaliser and cache table; the two
   client implementations of the seam; the server route; Deezer artist search
   with the album-overlap check. Shown on the desktop artist page header and
   the phone's artist rows, with the collage as fallback.
2. **Lyrics.** LRCLIB `/get` with `/search` fallback, the server route, the
   Lyrics tab on both heads showing a found lyric with its source and *Keep*.
   Plain lyrics only. A synced, scrolling lyrics view while playing is its own
   later piece of UI; the LRC text is stored from the start so it is there
   when that piece is built.
3. **Popularity.** MusicBrainz artist search for the MBID (or the tag, once
   `Importer` reads `MusicBrainzArtistId`), ListenBrainz top recordings
   matched to albums by `release_name` and to songs by title and duration,
   Deezer `rank` as the fallback, the catalog field. Shown as a mark on the top
   few songs of an album, and a "Popular" list on the artist page.
4. **Only if misses matter:** Fanart.tv as an optional keyed server source for
   artist pictures.

## Tests

What gets tests is the matching and the routing, never the network:
normalisation, song-by-title-and-duration, album choice among search results,
the artist-overlap check, ListenBrainz-then-Deezer fallback, caching a miss, and
which implementation of the seam a song gets (local song on a paired device →
local; server song → server). Sources are faked at the `HttpMessageHandler`.
No test calls a live service, and none asserts how a picture or a lyric looks
on screen.

## Open questions

- **A paired device whose server is unreachable but which has internet**:
  should it fall back to the local engine? Leaning no: one path per song keeps
  the owner's switch and the listener-privacy property honest, and the cache
  covers the songs that were already opened. But it is the one case where a
  listener sees less than a device on its own would.
- **Background warming**: phase 3 looks up popularity when a page opens. A
  server could instead walk the whole library slowly after a rescan, so the
  first visit to an album already has its marks. That is cheap at one request
  a second, but it is a lot of outbound traffic to start without being asked.
