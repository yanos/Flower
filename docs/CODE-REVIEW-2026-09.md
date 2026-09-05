# Code Review — September 2026

A whole-codebase read looking for performance, security, audio playback and
quality, memory, test-coverage, organisation and dead-code problems. Written
against `master` at `9ab685e` plus the uncommitted iOS-stutter work (the
`Play`/`SetUpcoming` identity guards, `ResourceMonitor`, the mobile diagnostics
readout).

This is **not** `ARCHITECTURE-REVIEW.md`. That document is the standing backlog
from the August structural review, and every tier in it is now marked done; this
one is a fresh pass looking for what has appeared since, or was never in its
scope. Where a finding here belongs to a subject that document already owns, it
says so.

## How to read this

Each finding carries the evidence that produced it. Where I could measure or
compute something rather than assert it, I did, and the numbers are in the
finding. Where a finding is reasoned from the code rather than observed running,
it says that too — the difference matters, because two of the items below are
things I *thought* were bugs on a first read and are not (see "Checked and
sound" at the end, which exists so nobody spends an afternoon re-deriving them).

Severity is about consequence to a listener or to the library, not about how
hard the fix is. Several of the worst ones are one-line changes.

---

## A. Will break something

### A1. The equalizer goes unstable at any output rate below 32 kHz

`Flower/Audio/Equalizer.cs:26` (`CenterFrequenciesHz`), `:47` (`BuildFrom`)

The 10 band centres are hard-coded up to 16 kHz, and `BuildFrom` is handed
`GaplessFormat.SampleRate` — whatever the output device negotiated
(`MiniaudioSink.OpenDevice` takes `device->sampleRate` and freezes it). Nothing
checks a band centre against Nyquist. When `centerFreqHz >= sampleRate / 2`, the
RBJ peaking formula produces `w0 >= π`, `alpha` collapses to zero or goes
negative, and the resulting biquad has its poles on or outside the unit circle.

Computed directly from the coefficient formulas in the file, feeding a single
unit impulse through the 16 kHz band at +6 dB and reading the output 400 samples
later:

| Device rate | `b0,b1,b2,a1,a2` | peak \|y\| | \|y\| at sample 400 |
|---|---|---|---|
| 48000 Hz | 1.1777, 0.8214, 0.4651, 0.8214, 0.6428 | 1.18 | 1.8e-39 |
| 44100 Hz | 1.1593, 1.0935, 0.5205, 1.0935, 0.6799 | 1.16 | 8.4e-36 |
| 32000 Hz | 1.0, 2.0, 1.0, 2.0, 1.0 | 1.0 | 4.4e-14 |
| **22050 Hz** | 0.6716, 0.4058, 1.9884, 0.4058, 1.6599 | **3.3e+43** | **1.3e+43** |
| 16000 Hz | 1.0, -2.0, 1.0, -2.0, 1.0 | 1.0 | 8.8e-14 |

At 22050 Hz the filter diverges by forty-three orders of magnitude inside nine
milliseconds. What the listener gets is full-scale noise — `OutputStage`'s clamp
bounds it to full scale, which is exactly the problem: it bounds it *at* full
scale, at whatever the volume knob is set to, into headphones.

32000 Hz and 16000 Hz are the degenerate cases rather than the divergent ones:
the coefficients reduce to a double integrator with both poles exactly on the
unit circle. Bounded for an impulse, but marginally stable — any DC component or
accumulated rounding error walks away without a restoring force, and the delay
line is never reset for the life of the `Equalizer` instance.

**Is a sub-32k rate reachable?** Not on the desktop paths, where 44.1/48 kHz is
universal. It is reachable in three places worth caring about: an Android device
reporting a native rate of 22050 or 32000 (some do, particularly on the
low-latency AAudio path for certain routes), a Linux ALSA/PulseAudio
configuration set to anything the operator likes, and an Apple route change onto
a rate Flower did not open with. Flower asks for `Playback` rather than
`PlayAndRecord` (`MiniaudioSink.ConfigureContextForPlatform` plus
`AppleAudioSession`), which is what keeps 8/16 kHz HFP out of the picture on
iOS, so iOS is the least exposed of the three.

The bar for a fix is low and the blast radius is high, which is a bad
combination to leave alone.

**Fix.** In `BuildFrom`, skip any band whose centre is at or above roughly
`0.45 * sampleRateHz` — a bypass, not a clamp, since squeezing four bands into
the top octave is not what the user asked for either. The `_coefficients` array
would carry an identity biquad, or better, `ProcessInPlace` would iterate a
shorter list. Worth logging once at construction when bands are dropped, since a
user whose top two bands stop responding deserves to be able to find out why.

### A2. `Retire()` during a track promotion deadlocks the UI thread

`Flower/Audio/RetargetableRingWriter.cs:187-202`,
`Flower/Audio/Ffmpeg/FfmpegTrackDecoder.cs:395`,
`Flower/Audio/GaplessCoordinator.cs:417,453,1001`

`RetargetableRingWriter.PromoteTarget` holds `_gate` for its whole run and, at
line 202, calls `newTarget.Write(...)` — the *blocking* ring write. That write
only returns when all the bytes land or the destination ring's generation
changes. Nothing else ends it. If the render callback has stopped draining the
shared ring, it does not return.

The class header explains at length why `Write` deliberately does **not** block
inside the ring while holding the gate, and parks on the gate's own monitor
instead. `PromoteTarget`, twenty lines further down, does the thing the header
says must not be done. In its own terms that is defensible — it is the drain,
and pacing it against playback is the point — but it makes `_gate` holdable for
an unbounded time.

`FfmpegTrackDecoder.Retire()` then takes that same gate, synchronously, at line
395, *before* the `Task.Run` that does the rest of the teardown off-thread. And
`GaplessCoordinator` calls `_current?.Retire()` from inside its own `_gate`, in
`Play` (417), `Stop` (453) and `Dispose` (1001).

The chain closes like this:

1. A track drains. `HandleDrainedOrFaulted` sets `_current = promoted` under the
   coordinator gate, releases it, and calls `promoted.PromoteTarget(_sharedRing)`.
2. Playback stops draining — the user pauses, or the output device disappears
   and `OnOutputDeviceLost` pauses for them. The 2-second shared ring fills from
   the promoted decoder's staged backlog (up to 60 s of it) and `PromoteTarget`
   parks, holding `RetargetableRingWriter._gate`.
3. The user presses next, or stop. `GaplessCoordinator.Play`/`Stop` takes the
   coordinator gate and calls `_current?.Retire()` — on the promoted decoder,
   the one whose writer gate is held.
4. `Retire()` blocks on `_writer.ResetTarget()`. The coordinator gate is now held
   by a blocked UI thread, and every subsequent coordinator call queues behind it.

The escape that ought to exist does not, because of ordering. Both `Play` and
`Stop` call `_sharedRing.Reset()` — which would bump the generation and release
`PromoteTarget` — but they call it *after* `_current?.Retire()`, at lines 419 and
457 respectively. The unlock is on the far side of the lock.

`GaplessAudioManager.Stop()` makes this more likely rather than less: it calls
`_sink.Stop()` first (stopping the callback, guaranteeing the ring stops
draining) and `_coordinator.Stop()` second.

Reasoned from the code, not reproduced. The window is narrow — it needs a
pause or a device loss inside the promotion — but "press stop during a track
change" is not an exotic gesture, and the failure is a hung UI rather than a
glitch.

**Note this is the default decoder.** `TrackDecoder` (LibVLC) does its teardown
entirely inside a `Task.Run` and never touches the writer gate on the caller's
thread, so it is not exposed. `FfmpegTrackDecoder` is what ships.

**Fix.** Two independent changes, and both are worth having:

- Move `_sharedRing.Reset()` **above** `_current?.Retire()` in
  `GaplessCoordinator.Play` and `Stop`. That alone breaks the cycle: the
  generation bump releases `PromoteTarget` before anything asks for its gate.
  Careful with `Play`'s `preserveTail` branch, which deliberately does not reset —
  that branch only runs when `_current == null`, so there is nothing to retire
  and nothing to deadlock.
- Give `PromoteTarget`'s drain a bound. It already knows how to stop on a
  generation change; an `isAbandoned`-style predicate, or a deadline, would stop
  it depending on someone else to notice.

---

## B. Correctness and resource handling

### B1. Sync request bodies are materialised three times, before authentication

`Flower.Server/Endpoints/SyncEndpoints.cs:117-126` (the filter), and all four
POST handlers: `:329` (`ApplyPlaylists`), `:374` (`ReportLog`), `:416`
(`ReportPlays`), `:457` (`ReportTrackState`)

The group's endpoint filter buffers the whole request body so the signature can
cover the bytes that actually arrived — correct, and necessary. It does it into
a `MemoryStream`, then calls `.ToArray()`, then rewinds `Request.Body`.

The handlers then read the body **again**, through
`new StreamReader(context.Request.Body)` and `ReadToEndAsync()`, and deserialize
from the resulting `string`.

For one 20 MB request (`MaxBodyBytes`, matching Kestrel's own cap) that is:

- ~20 MB in the `MemoryStream`'s internal buffer,
- ~20 MB more from `ToArray()`,
- ~40 MB more as a UTF-16 `string` from `ReadToEndAsync()`,
- plus the deserialized object graph.

Roughly 80 MB of allocation, most of it on the large object heap, for a 20 MB
payload — and the first 40 MB of it happens **before** the signature is checked,
because the filter buffers before it authenticates. It has to; there is no way
to verify a body signature without the body. But that makes body size an
unauthenticated resource decision.

`BulkLimiter` bounds it at 20 requests per 60 s per source /64, so this is not a
trivially-triggered outage. It is still eighty megabytes of garbage per request
where twenty would do, on a server the deployment model says is "hardware the
owner already has".

**Fix.** The filter already holds the exact bytes. Stash them in
`HttpContext.Items` beside `AuthenticatedFingerprintKey` and have the handlers
deserialize from that `byte[]` (or a `ReadOnlySpan<byte>`) with
`JsonSerializer.Deserialize`. That removes the second read and the UTF-16 string
outright. `ToArray()` can go too — `JsonSerializer` will read a `MemoryStream`,
and `SignedRequestCanonicalizer` could take a span.

### B2. `NonceReplayGuard` prunes the whole dictionary on every insert

`Flower.Core/Services/NonceReplayGuard.cs:22-38`

`TryRecord` calls `Prune`, and `Prune` enumerates the entire `_seen` dictionary
looking for expired entries. Every authenticated request pays a full scan.

Measured, inserting distinct nonces at a fixed timestamp so nothing is evicted
(which is the shape of a burst arriving inside one retention window):

```
   1000 inserts:      40 ms  (0.0404 ms/insert)
   5000 inserts:     238 ms  (0.0476 ms/insert)
  20000 inserts:    1167 ms  (0.0584 ms/insert)
  60000 inserts:   10874 ms  (0.1812 ms/insert)
```

Quadratic, as expected. **Calibration matters here, so: under Flower's own rate
limits this is not currently a bottleneck.** The /rest limiters total 660
requests per minute per source, which at a 120 s retention is a steady state of
about 1,300 entries — around 40 µs per request. Real, wasteful, not a crisis.

What makes it worth fixing anyway is the shape rather than today's number.
`SignatureVerifier.Verify` records the nonce **before** it checks the signature
(deliberately, and correctly — the comment at
`Flower.Core/Services/SignatureVerifier.cs:31-35` explains why), so `_seen` is a
dictionary an unauthenticated caller writes to, keyed on values it chooses. The
cost per insert grows with how much has been inserted. That is precisely the
property `RateLimiter` next door goes out of its way to avoid — it throttles its
sweep to once per window behind an `Interlocked` guard and documents
attacker-chosen keys as a memory sink it is bounding. The two classes disagree
about the same hazard.

**Fix.** Copy `RateLimiter.Prune` — a `_nextPrune` timestamp and an
`Interlocked.Exchange` guard, five lines. Consider also a hard entry ceiling
after which `TryRecord` refuses rather than grows.

### B3. `Library.Snapshot` can publish a stale snapshot that never expires

`Flower.Core/Models/Library.cs:111-123`

The getter is deliberately lock-free:

```csharp
var current = Volatile.Read(ref _snapshot);
if (current is not null)
    return current;

var built = LibrarySnapshot.Build(Tracks);
Volatile.Write(ref _snapshot, built);
return built;
```

The comment reasons about two threads racing to *build*, and concludes the
loser's copy is discarded — which is true and fine. It does not cover the race
against a concurrent **invalidation**:

1. Reader sees `_snapshot == null` and reads `Tracks` — the pre-rescan list.
2. Rescan replaces `Tracks` wholesale and sets `_snapshot = null`. It is already
   null, so the invalidation is a no-op.
3. Reader finishes building from the list it captured in step 1 and publishes it.

The snapshot now describes the old catalog, and nothing will invalidate it until
the *next* mutation — which on a server that has just finished its startup
rescan may be a long time. Every `/rest` browse, search and `Find(id)` reads
through `Snapshot`, so the symptom is a library that looks like it did before the
scan, indefinitely.

Narrow: it needs a request to arrive inside the build window of a rescan.
`Flower.Server` runs its first rescan before `app.Run()`, so startup is safe;
`LibraryRescanCoordinator`'s later passes are not. Reasoned from the code, not
observed.

`_byPath` (line 96) has the same structure and the same gap.

**Fix.** Version the invalidation rather than nulling it: keep a counter bumped
by `InvalidateIndexes`, read it before building, and only publish if it has not
moved. Roughly the pattern `GaplessRingBuffer` already uses for its generation.

### B4. `SeekableHttpStream` cannot be cancelled, and parks a decode thread for up to two minutes

`Flower.Core/Services/SeekableHttpStream.cs:577` (`EnsureBodyAt`), `:202-254`
(`SendAsync`), `:174` (`EnsureProbed`)

`EnsureBodyAt` issues its GET with `CancellationToken.None`. `SendAsync`'s 429
loop then waits out `ThrottleWaitBudget` — 120 seconds by default — with that
same non-token, in `Task.Delay(pause, cancellationToken)`.

So a stream that is being throttled holds its decode thread for up to two
minutes, and nothing can interrupt it. `Retire()` cannot: `FfmpegTrackDecoder`
resets the writer and then joins with a 5 s timeout, after which it logs *"did
not stop within 5s; leaking its decoder rather than closing it underneath"* and
leaks the native decoder and the stream. A user skipping through five tracks
during a throttle storm leaks five.

This is the same failure the 429 handling was built to fix, one level up: the
handling is right (a throttle is not a broken stream, and waiting is correct),
but the wait was made uninterruptible.

`Read`'s `Thread.Sleep(RetryBackoffMs)` at line 517 is the same shape, three
times 250 ms.

**Fix.** Give the class a `CancellationTokenSource`, cancel it from `Dispose`,
and thread it through `SendAsync`, `EnsureBodyAt`, `ReadBody` and `SkipForward`.
`FfmpegTrackDecoder.Retire` already disposes `_remoteStream` — it just does it on
the far side of a join that the stream is what is blocking.

### B5. Non-retryable HTTP statuses are retried as though they were transient

`Flower.Core/Services/SeekableHttpStream.cs:578`, `:495-505`

`EnsureBodyAt` calls `response.EnsureSuccessStatusCode()`, which throws
`HttpRequestException` for a 401, 403 or 404. `Read`'s catch clause treats
`HttpRequestException` as a dropped connection: three reopen attempts, 250 ms
apart, then latch `_broken`.

The class already knows this distinction matters. `HttpProtocolErrorException`
exists precisely so a response that "arrived intact and is not the track" fails
at once rather than three times, and its catch clause says so. An expired
signature, a revoked device or a deleted track is the same category — asking
again produces the same answer.

Consequence is modest: 750 ms and two pointless requests against a server that
has already said no, per track. It matters most in the case it is most likely to
happen in — a revoked pairing, where every remaining queued track pays it.

**Fix.** Catch `HttpRequestException` with a `StatusCode` in {401, 403, 404, 410}
before the general clause and latch immediately, mirroring the
`HttpProtocolErrorException` arm.

---

## C. Security and the trust boundary

`CLAUDE.md` is explicit that this is the one area where "it's just for me"
reasoning does not apply: other people get accounts, and some of them listen from
outside the house. These are read against that standard, not against a
single-user one.

### C1. There is no authorisation model on the Subsonic surface, only authentication

`Flower.Server/Endpoints/SubsonicEndpoints.cs:396` (`ToDto`), `:499`
(`DeletePlaylist`), `:435` (`UpdatePlaylist`), `:509` (`SetStarred`)

Every credential that authenticates gets the same powers. A friend's phone,
holding a per-client credential from `SubsonicCredentialStore`, can:

- `deletePlaylist` any playlist, including the owner's;
- `updatePlaylist` any playlist — rename it, empty it, flip it public;
- `star`/`unstar` anything;
- `scrobble` arbitrary ids, inflating play counts that
  `ARCHITECTURE-REVIEW.md` itself calls out as existing nowhere else.

`ToDto` returns `null` for the owner field with the comment *"Flower has no user
model — see the auth notes above — so there is nobody to name"*, which is an
accurate description of the current state and also the finding. Path B
credentials are per-client and individually revocable, which was the right call
and solves the *revocation* half; what is missing is the *scoping* half.

Nothing here is exploitable by a stranger — it needs a credential the owner
issued. But per-client credentials exist because the owner is expected to hand
them to people, and the difference between "my sibling can listen" and "my
sibling can delete my playlists" is the kind of thing that only becomes
interesting after it has happened once.

**Not a small fix**, and possibly not one worth taking yet — it means a user
column on playlists and a decision about whether a listener's stars are theirs or
the library's. Worth writing down as a deliberate gap rather than an oversight.
The cheap intermediate step is a read-only flag on a credential, which covers the
destructive half without needing a user model at all.

### C2. Client log ingestion has no size bound

`Flower.Server/Endpoints/SyncEndpoints.cs:371-394`,
`Flower.Core/Services/ClientLogStore.cs:31`

`ReportLog` accepts `report.Entries` with no cap on count or total size, stores
it under the caller's fingerprint, and `ClientLogStore` retains for 7 days.
Bounds are `MaxBodyBytes` (20 MB) and `BulkLimiter` (20 requests / 60 s / source)
— so one paired device can commit roughly 400 MB per minute of disk, held for a
week.

Retention is purely temporal; there is no per-device quota and no total ceiling.
On the "server the owner already has" this is a disk-exhaustion path that needs
no exploit, just a client with a logging bug.

**Fix.** A per-fingerprint byte or line quota in `SetSnapshot`, dropping oldest
first — which is the shape the store already has for time-based eviction. A total
store ceiling as a second backstop.

### C3. `getAlbumList2` does not clamp `size`

`Flower.Server/Endpoints/SubsonicEndpoints.cs:325`

```csharp
.Take(size <= 0 ? 500 : size)
```

`size` comes straight off the query string with no upper bound. The Subsonic
protocol caps it at 500; a client asking for `size=1000000` gets the entire album
catalog serialised in one response, from a route budgeted by `BrowseLimiter` at
120/minute. Amplification rather than a vulnerability, and one line to fix:
`Math.Clamp(size, 1, 500)`.

`offset` is unbounded too, but `Skip` on a materialised list is cheap enough not
to matter.

### C4. Attacker-controlled identity strings become dictionary keys unbounded

`Flower.Core/Services/NonceReplayGuard.cs:25`

```csharp
_seen.TryAdd($"{fingerprint}:{nonce}", now);
```

Both halves come from request headers, before verification, with no length
check. Kestrel's default header limits (32 KB total) are the only bound, so an
entry can be tens of kilobytes rather than the ~80 bytes a real one is. Combined
with B2's absent prune throttle, that is the memory sink pointed at the one
dictionary that has no ceiling.

**Fix.** Reject a fingerprint longer than the hash it is supposed to be, and a
nonce longer than `DeviceSigningKey` generates, before either reaches a key. Both
are fixed-width by construction on the honest path.

---

## D. Performance and memory

### D1. Every oversized album cover is decoded twice

`Flower/Services/AlbumArtLoader.cs:171-180`

```csharp
var full = new Bitmap(stream);
if (full.PixelSize.Width <= MaxArtPixels)
    return full;

full.Dispose();
stream.Position = 0;
return Bitmap.DecodeToWidth(stream, MaxArtPixels);
```

The first decode exists only to read `PixelSize.Width`. For the 1400×1400 art
the comment says a modern library is routinely made of, that is a full ~7.8 MB
RGBA decode plus the JPEG/PNG decompression cost, thrown away immediately, once
per cover.

The comment is honest about this — *"there is no cheap way to read the intrinsic
size first (SkiaSharp, and so SKCodec, isn't a reference of this project)"* — and
that was true of the options considered. But intrinsic size does not need a codec:
it is in the file header. PNG puts width and height at bytes 16-23 of the IHDR
chunk; JPEG needs a short walk of the segment markers to the SOFn; both are
well under thirty lines and neither decodes a pixel. Adding a
`TryReadIntrinsicWidth(Stream)` and only decoding once would remove the whole
first pass.

This is the most expensive single thing in the cold-scroll path, and cold scroll
on a phone is where the user's heat complaint started.

### D2. `Retain` sweeps the entire weak cache on every hit and every miss

`Flower/Services/AlbumArtLoader.cs:126-130`

The dead-entry sweep runs unconditionally at the end of `Retain`, and `Retain` is
called from `TryGetCached` on every cache **hit** as well as after every load. So
displaying a tile whose art is already cached walks all ~1400 `WeakReference`s.

Honest sizing: a `TryGetTarget` is a handle dereference, so 1400 of them is on
the order of ten microseconds. At forty tiles a second that is well under a
millisecond per second of scrolling. **This is waste, not a hotspot**, and I
would not have listed it except that it sits directly beside D1 in the same
method chain and the fix is the same three lines as B2 — a throttle, so the sweep
runs on a schedule rather than per call.

The `StrongCacheLock` being a process-global `lock` around every art lookup is
the more interesting half: it serialises the UI thread against every background
art load. Uncontended it is a few nanoseconds; under a cold scroll with loads
completing on thread-pool threads it is a real serialisation point.

### D3. Every scroll event allocates a `SortedSet` and a `List`

`Flower/Controls/MusicListPanel.cs:126-143`

`ComputeRenderIndices` builds a `SortedSet<int>` (a red-black tree, one node
allocation per element) and then a `List<int>` from it, on every scroll. About
thirty nodes plus two objects per event, at scroll-event rates.

The set is doing two jobs — dedupe and sort — that the data does not need.
Indices are visited in ascending order, and `_groupLeader[i]` is by construction
`<= i` and is the same value for every row in a group, so at most one leader per
group is ever added ahead of the window. A pre-sized `List<int>` seeded with the
leader of the first visible row, then filled linearly, gives the same result with
one reusable buffer and no tree.

Gen0 pressure during scrolling is exactly the kind of cost that shows up as heat
rather than as a visible stall, which is what makes it worth a mention.

### D4. `MiniaudioSink`'s watchdog timer runs for the life of the process

`Flower/Audio/MiniaudioSink.cs:252-254`

The 1 Hz `Timer` starts in the constructor and stops in `Dispose`. It returns
early when `_ringBuffer` is null, so it is cheap when nothing has started — but
it wakes the process once a second forever, including when the app is idle in the
background on a phone. It is also `System.Timers.Timer` with the default
`AutoReset`, so nothing prevents two ticks overlapping if a logging call is slow;
`FoldBridgeCounters` resets the native snapshot and the comment says it *"must
happen exactly once per watchdog tick and nowhere else"*, which two overlapping
ticks would violate.

**Fix.** Start it from `Start()` and stop it from `Dispose`/`CloseDevice`; set
`AutoReset = false` and re-arm at the end of the handler.

### D5. `ResourceMonitor.ThreadCount` cannot see the threads that matter

`Flower.Core/Diagnostics/ResourceMonitor.cs:160`

```csharp
private static int ThreadCount() => System.Threading.ThreadPool.ThreadCount + 1;
```

This is my own code from the current uncommitted work, and it is the weakest part
of it. `ThreadPool.ThreadCount` counts pool threads. The threads a decode leak
would actually show up in — `FfmpegTrackDecoder`'s decode thread, `AudioFeeder`'s
feeder thread, LibVLC's internal threads — are dedicated `Thread` objects and
native threads, none of which are in the pool. The `+ 1` is a fudge for the main
thread and does not change that.

So the number is real but answers a narrower question than the label on it
suggests, and a decoder thread that fails to stop (the leak `Retire`'s 5 s join
timeout explicitly logs) will not move it at all.

**Fix.** Either count real threads on the platforms that can
(`Process.GetCurrentProcess().Threads.Count`, which does drag
`System.Diagnostics.Process` in) or — better and cheaper — have the decoders
maintain their own live-instance counter and report that, since "how many
decoders are alive" is the actual question.

---

## E. Dead code and organisation

### E1. `LibVlcRawStreamSink` and `GaplessRingBufferStream` are 291 lines of unreachable code

`Flower/Audio/LibVlcRawStreamSink.cs` (223 lines),
`Flower/Audio/GaplessRingBufferStream.cs` (68 lines)

Nothing constructs either. Every reference to `LibVlcRawStreamSink` in the
codebase is a comment; `GaplessRingBufferStream` is referenced only from
`LibVlcRawStreamSink` itself. Neither has a single test.

`CLAUDE.md` records the intent — *"kept unreferenced as a fallback for one
release cycle"* — so this is a decision, not an oversight. Two observations for
when that cycle is judged to be over:

- The fallback is not a working fallback. It has no construction path, no
  configuration switch, and no test that would notice if it stopped compiling
  correctly. Restoring it would be a code change either way, and `git` holds it
  better than `Flower/Audio/` does.
- The five comments that reference it are the useful part — the `amem` history,
  the seek-freeze that motivated the move to miniaudio. Those are worth keeping
  in `MiniaudioSink`'s own header when the file goes.

### E2. `GaplessFormat` is process-wide mutable static, and tests run in parallel

`Flower/Audio/GaplessFormat.cs:71-78`

`ConfigureSampleRate` and `ConfigureSampleFormat` mutate statics that everything
in the audio pipeline reads. The comment is careful about the runtime contract —
both are set before the first decoder exists and not touched afterwards — and
that holds for the app.

It does not hold for the test suite, where xUnit runs collections in parallel by
default. Any test that configures the format changes it under every concurrently
running test that reads `GaplessFormat.SampleRate` or `BytesPerFrame`. The
`Flower.DeviceChecks` note in `CLAUDE.md` already records having been bitten by
exactly this — *"Each decoder is handed its own sample format rather than the run
moving `GaplessFormat`'s process-wide one"* — and `OutputStage` shows the right
answer: it takes both as constructor parameters and caches them, with a comment
explaining that this is what lets a test build a stage in either format.

Worth spreading that pattern rather than relying on nobody adding a parallel test
that touches it. `GaplessCoordinator`, `MiniaudioSink` and the decoders all read
the statics directly.

### E3. The mobile shell never got Tier 4.2's decomposition

`Flower/ViewModels/Mobile/MobileMainViewModel.cs` — 1609 lines, 189 members

`ARCHITECTURE-REVIEW.md` Tier 4.2 decomposed `MainViewModel`, and marks itself
done. `MainViewModel` is nonetheless still 1885 lines with 220 members, and its
mobile counterpart — which was never in that tier's scope — is 1609 with 189.

The good news, which I checked rather than assumed: they are not duplicates. Only
8 public member names are common to both (`Dispose`, `IsShowingAlbumGrid`,
`IsShowingTrackList`, and the five transport commands), against 157 desktop and
116 mobile. These are two genuinely different shells over shared services, which
is the right shape. They are simply both large.

Not urgent. Recorded because "Tier 4.2 — DONE" reads as though the shell-sized
ViewModel problem was solved, and half of it was never in scope.

### E4. `GaplessAudioManager`'s public constructor defaults to the wrong decoder

`Flower/Audio/GaplessAudioManager.cs:58` and
`Flower/Audio/GaplessCoordinator.cs:165` —
`TrackDecoderKind decoderKind = TrackDecoderKind.LibVlc`

`CLAUDE.md` is emphatic that **FFmpeg is the default, on every head**, because
LibVLC's `amem` truncates to 16 bits whatever is asked of it. The composition
root passes the elected kind explicitly, so the app is fine. But the parameter
default silently disagrees with the app's default, and a caller that omits it —
a test, a device check, a future head — gets LibVLC and a 16-bit ceiling with no
warning anywhere.

`GaplessCoordinator`'s own constructor has the same default, at line 165.

**Fix.** Make it required, or default it to `DecoderElection`'s answer. A default
that contradicts the documented default is a trap regardless of which way it is
resolved.

### E5. `MiniaudioSink.Start` leaks a `GCHandle` if called more than once

`Flower/Audio/MiniaudioSink.cs:453`

```csharp
_selfHandle = GCHandle.Alloc(this);
```

Unconditional, and never checked for an existing allocation. If `Start` runs
twice — including the case where the first call bailed after `context_init`
failed, which returns without freeing — the previous handle is overwritten and
leaks, keeping the old sink alive for the process lifetime.

Nothing calls `Start` twice today; `GaplessAudioManager.StartSink` calls it once
during construction. Cheap to make safe (`if (!_selfHandle.IsAllocated)`), and
the failure mode if it ever does happen is a rooted object graph holding a ring
buffer and a device, which is not the kind of leak that announces itself.

### E6. Promoted decoders keep the event handlers they were given while armed

`Flower/Audio/GaplessCoordinator.cs:659-660` (armed wiring), `:815-821`
(promotion wiring)

`ArmAsync` subscribes `HandleArmedDrained` and `HandleArmedFaulted`. When the
decoder is promoted, `HandleDrainedOrFaulted` subscribes `Drained`, `Faulted` and
`SeekSettled` again, without removing the armed pair. Both fire on the eventual
drain; the armed handlers no-op because `_armed` no longer references the
decoder, so the behaviour is correct.

Cosmetic, and bounded — a decoder is promoted once. Listed because the two
handler sets having different guard conditions (`_armed` vs `_current`) is the
sort of thing that stops being harmless the moment a third role appears.

### E7. `Process.Start` is called directly, and two platforms cannot run it

`Flower/Views/MainView.axaml.cs` (`LocateFile`),
`Flower/ViewModels/MainViewModel.cs` (`OpenDatabaseLocation`)

Both branch macOS / Windows / else-as-Linux and call `Process.Start`. That is
unusable on iOS (sandboxed) and unsupported on Android, so on the two platforms
where it is wrong the menu item is present and silently does nothing.

The fix is an `IPlatformShell.TryRevealInFileManager(path)` registered per
platform, with today's logic moved behind it on desktop, `false` on mobile, and
the caller hiding the affordance rather than failing quietly. Small, low risk.

Inherited from `CROSS-PLATFORM-PLAN.md` item #2, the last unbuilt item in that
document, which is why the document is gone and this is here. Its item #8
(vendor the LibVLC natives for macOS) went with it as void rather than open —
there is no LibVLC to vendor.

### E8. Four loose ends in the album grid

`Flower/Controls/AlbumGridRowControl.axaml(.cs)`, `Flower/Views/AlbumGridView*`,
`Flower/ViewModels/Mobile/AlbumTileViewModel.cs`

Independent, low-risk, none a blocker; recorded because they were about to stop
being written down anywhere:

- `RebuildRows` re-chunks on every `SizeChanged`, so a resize snaps between
  column counts instead of reflowing. Needs a debounce.
- Expansion height is a hardcoded per-row estimate (`TrackRowHeight = 26`)
  rather than measured, because Avalonia will not animate to or from `Auto`. It
  drifts silently if the row template changes.
- No keyboard navigation in the grid.
- `AlbumTileViewModel` and friends still live under `Flower.ViewModels.Mobile`
  although desktop depends on them directly.

Inherited from `ALBUM-GRID-PLAN.md`, which was a design record for shipped work
and had nothing else left in it.

---

## F. Where more testing would have paid

The suite is genuinely broad — 1685 fast tests, plus the `RequiresLibVLC`,
`RequiresFfmpeg` and device-check tiers, and the patterns
(`TestSupport/` fakes, synthetic WAV fixtures, `PlatformDataDirectory` pinning)
are right. These are the specific gaps the findings above walked through.

1. **The equalizer at any rate but 48 kHz.** `EqualizerTests` uses a single
   `SampleRate` constant. A parameterised pass at 44100, 32000 and 22050 —
   asserting the impulse response stays bounded — is about ten lines and catches
   A1 outright. This is the highest-value test in the list.

2. **`Retire()` racing `PromoteTarget`.** `RetargetableRingWriterTests` exists
   precisely because *"what happens when a write and a retarget collide on a full
   ring"* cannot be scheduled by a real-decode test. The same argument applies to
   a retire colliding with a promotion into a ring nobody is draining, and it is
   testable in exactly the same way. Catches A2.

3. **`Library.Snapshot` under concurrent invalidation.** A test that reads
   `Snapshot` on one thread while `UpdateTracks` runs on another, then asserts the
   snapshot eventually reflects the new tracks. Catches B3.

4. **Cancellation of `SeekableHttpStream`.** `StreamingNetworkOutageTests` and
   `SeekableHttpStreamTests` cover the 429 and reopen behaviour; neither covers
   disposing a stream that is mid-wait. `LoopbackMediaServer.RefuseBodiesWith429`
   already provides the fixture. Catches B4, and would catch the decoder leak it
   causes.

5. **Playlist authorisation.** There is nothing to test until there is a model
   (C1), but `SubsonicEndpointTests` asserting that a second credential *can*
   currently delete the first's playlist would at least pin the present behaviour
   as a decision.

6. **`NonceReplayGuard` has no dedicated test file.** It is exercised indirectly
   through `PeerSignatureAuthTests` and five others. A direct one covering
   expiry, the fingerprint-scoping of keys, and — once B2 is fixed — that the
   prune is throttled, would be worth having for a class this load-bearing.

7. **Already known and recorded, not re-litigated here:** the native CoreAudio
   PCM-shape counters have no S24 path, so `MaxSampleDelta`, `AbruptFrames`,
   `RepeatedBuffers` and `MaxRepeatedBufferRun` are blind under the default
   decoder. The managed side now prints `n/a` rather than a misleading `0`; the
   real fix needs the vendored `miniaudio.framework` / `libminiaudio.so`
   rebuilt. See `native/miniaudio/README.md`.

---

## Checked and sound

Things that look like defects on a first read and are not. Recorded so the next
review does not spend its time here.

- **`OutputStage`'s fade state being written from two threads.** `BeginFadeIn`
  and `ArmFadeIn` write `_fadeInLength`, `_fadeInRemaining`, `_fadeOutLength` and
  `_fadeOutRemaining` from the UI thread, and the render callback owns all four.
  It is safe: both callers are `MiniaudioSink.Resume`/`Pause`/`Stop`, all under
  `_gate`, and all with the device stopped — `ma_device_stop` waits for the
  callback to return before it does. A divide-by-zero on
  `_fadeOutRemaining / (float)_fadeOutLength` is not reachable.

- **`GaplessRingBuffer`'s generation protocol.** The index-before-generation
  publication order, the "counterpart has not rebased yet, treat as empty" rule,
  and the post-copy generation re-check are all correct, and the `Interlocked`
  rather than `Volatile` choice for the 64-bit indices is right for the armeabi-v7a
  build the comment cites. The one thing worth knowing is that
  `UnderrunCount` counts pre-playback idle reads, so a non-zero value before the
  first track is normal and not evidence of anything.

- **`Library.Tracks` being enumerated outside the lock.** Safe, because every
  mutation replaces the list wholesale (lines 232, 256, 392, 474, 661) rather
  than mutating in place. The stale-snapshot race in B3 is a different problem
  and does not involve a torn enumeration.

- **Smart playlist recomputation looping forever.** `PlaylistsChanged` triggers
  `Schedule()`, and `Refresh()` can change playlists — but `LimitSelector.Random`
  draws from a `Random` seeded per playlist id (`SmartPlaylistEvaluator.SeededFor`),
  so a pass over an unchanged library produces an identical result and the
  fixed point holds. This was thought about.

- **The `Play`/`SetUpcoming` identity guards** added in the current uncommitted
  work. `Track.Id` is a per-instance `Guid` that `Clone()` preserves and
  `Library.CarryForwardMutableState` explicitly carries across a rescan
  (`Flower.Core/Models/Library.cs:543`, with `RescanCarryForwardGuardTests`
  protecting it), so two live spellings of one track compare equal and two
  different tracks never do. Repeat-one is unaffected: the guard only fires when
  `_current != null`, and a track that has drained has already cleared it.

- **`RateLimiter`'s sliding window.** The weighted previous/current
  approximation is the standard one and is implemented correctly, including the
  compare-and-retry against `ConcurrentDictionary`. `KeyFor` collapsing IPv6 to
  /64 while leaving link-local at full precision is the right call for both
  reasons its comment gives.

- **`async void`.** Nineteen occurrences, all genuine event handlers, all in
  Views. The two non-handler cases were already converted and carry comments
  saying why.

- **Swallowed exceptions.** One empty catch in the entire client and server
  (`LibraryBrowserViewModel.cs:553`, `catch (OperationCanceledException) { }`),
  which is correct. Everything else logs.

---

## Suggested order

1. **A1** — one guard in `Equalizer.BuildFrom`, plus the parameterised test from
   F.1. Highest consequence, lowest cost, and the test is what stops it
   returning.
2. **A2** — reorder `_sharedRing.Reset()` above `_current?.Retire()` in
   `GaplessCoordinator.Play` and `Stop`. Bound `PromoteTarget`'s drain as the
   second, independent half.
3. **B4** — cancellation through `SeekableHttpStream`. Fixes a decoder leak as
   well as a two-minute park, and F.4's fixture already exists.
4. **B1** — deserialize the sync handlers from the bytes the filter already
   holds. Mechanical, and removes 60 MB of allocation per large request.
5. **B2** + **C4** together — throttle the prune, bound the key lengths. Five
   lines each, copied from `RateLimiter`.
6. **C3**, **E4**, **E5** — one-liners.
7. **D1** — intrinsic-size-from-header, removing the double decode. The biggest
   single win in the cold-scroll path.
8. **B3** — version the snapshot invalidation.
9. **C2** — a per-device log quota.
10. **B5**, **D3**, **D4**, **D5**, **E1**, **E6**, **E7**, **E8** — when
    convenient.
11. **C1**, **E2**, **E3** — genuine design questions rather than fixes, and
    worth a decision recorded here rather than a patch.
