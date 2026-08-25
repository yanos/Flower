using System;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using LibVLCSharp.Shared;

using Flower.Models;

namespace Flower.Manager
{
    // Owns the decode-ahead/handover state machine that makes track
    // transitions sample-accurate: a "current" TrackDecoder writes directly
    // into the shared ring buffer a render sink (IAudioSink) reads from; a
    // "decode-ahead" TrackDecoder (armed via SetUpcoming) decodes the next
    // track into its own private staging ring the moment it's known, well
    // before it's needed. When the current decoder's drain callback fires
    // (its samples are exhausted), the armed decoder - if it's ready - is
    // spliced directly into the shared ring's write cursor and promoted to
    // "current", so the render sink never sees a gap or a reconfiguration.
    //
    // EndReached fires at exactly the same moment/meaning VlcAudioManager's
    // did: once per track, when its decode is exhausted - regardless of
    // whether a gapless handover happened underneath it. This is what lets
    // PlaylistControlViewModel's existing EndReached handler (play-count,
    // library save, computing the next track, calling Play() again) keep
    // working unmodified; Play() is idempotent against a track that already
    // became current via a natural handover (see Play()).
    public sealed class GaplessCoordinator : IDisposable
    {
        // Generous cap so decode-ahead - which starts as soon as the current
        // track begins, not "a few seconds before it ends" - has room to run
        // well ahead of playback without unbounded memory growth. Doesn't
        // rely on any assumption about how fast LibVLC's callback-mode
        // decode paces itself relative to real time.
        public const int DefaultStagingCapacityBytes = 60 * (int)GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame;

        // Only ever non-default in tests, which shrink it so decode-ahead
        // fills it in a fraction of a second - the "staging ring already
        // full when the handover arrives" case, which at 60s would need a
        // minute-long fixture to reach. See
        // GaplessCoordinatorRealDecodeTests.
        private readonly int _stagingCapacityBytes;

        private readonly GaplessRingBuffer _sharedRing;
        private readonly Func<Track, GaplessRingBuffer, ITrackDecoder> _currentDecoderFactory;
        private readonly Func<Track, GaplessRingBuffer, ITrackDecoder> _armedDecoderFactory;
        private readonly object _gate = new();

        // Second, independent LibVLC core used exclusively for the armed
        // (decode-ahead) role - only non-null when constructed via the
        // LibVLC-backed constructor below. GaplessCoordinatorRealDecodeTests
        // proved that two MediaPlayers (current + armed, both
        // SetAudioCallbacks-based) alive on one shared LibVLC core is enough
        // to make the current decoder's OnDrain - and even its higher-level
        // EndReached - silently fail to fire roughly 80% of the time, no
        // matter how long it's given; adding diagnostic logging reliably
        // masked the race every time, the signature of a genuine
        // timing/scheduling issue inside LibVLC's own event dispatch under
        // concurrent SetAudioCallbacks players, not an application-level
        // bug. Giving the armed role a completely separate core removes that
        // contention outright rather than trying to detect or paper over it
        // with a coarser timeout. _currentCoreIndex/_cores below make
        // current and armed always land on different cores even as their
        // roles swap across repeated handovers (see Play/SetUpcoming/the
        // promotion branch of HandleDrainedOrFaulted).
        private readonly LibVLC? _secondCore;
        private readonly LibVLC[]? _cores;
        private int _currentCoreIndex;

        private ITrackDecoder? _current;
        private string? _currentPath;

        // A decoder's own BytesProduced is decode progress, not playback
        // progress: it runs way ahead of real time for a track that decoded
        // ahead into its private staging ring before being promoted (the
        // common case - arming starts the instant the *previous* track
        // begins, not a few seconds before it ends), and can be completely
        // frozen at its final value from the moment of promotion onward if
        // that decode-ahead had already finished - PromoteTarget only moves
        // already-decoded bytes into the shared ring, it doesn't produce new
        // ones. That made an earlier baseline-subtraction scheme built on
        // BytesProduced read as permanently stuck at zero for an entire
        // track after a handover, even though it was playing correctly the
        // whole time (found from a real "scrubber stuck at 0:00" report).
        //
        // _sharedRing.TotalBytesRead doesn't have that problem: it only
        // advances as fast as the render sink actually drains the ring, so
        // it tracks real elapsed playback time unconditionally, independent
        // of how far ahead decode raced. _currentTrackReadSplit is the
        // TotalBytesRead value that corresponds to zero elapsed time for
        // whatever track is current - 0 for a freshly Play()'d track (the
        // ring was just Reset()), the ring's TotalBytesWritten at the exact
        // moment of promotion for a natural handover (the byte offset where
        // the new track's audio begins in the ring's stream - see the
        // promotion branch of HandleDrainedOrFaulted), or a negative offset
        // equal to the seek target for Seek() (see its remarks), re-anchored
        // onto the real landing point by HandleSeekSettled once the seek
        // settles.
        private long _currentTrackReadSplit;

        private ITrackDecoder? _armed;
        private Track? _armedTrack;
        private GaplessRingBuffer? _stagingRing;

        // Set if the armed decoder reaches Drained on its own, before the
        // current track does (real for a short next-up track decoding into
        // a much larger staging ring - it can legitimately finish well
        // ahead of a still-playing current track). A decoder only ever
        // drains once, so once this has happened there's no future Drained
        // event left to wire a handler to - promotion has to notice this
        // flag and synthesize the completion itself instead of waiting for
        // an event that will never come.
        private bool _armedAlreadyDrained;

        private int _generation;

        // Diagnostic-only, temporary: null in every unit test (the
        // (GaplessRingBuffer, factory) constructor never passes one), so
        // every call below goes through _logger?. to stay a no-op there.
        private readonly ILogger<GaplessCoordinator>? _logger;

        // Fired once per track, when its decode is exhausted (or it faulted
        // mid-decode) - see class remarks.
        public event Action<Track>? EndReached;

        // The current track stopped because its decode failed, not because it
        // finished - see HandleDrainedOrFaulted. Follow-up behavior (promote
        // the armed track, or stop) is identical; only the reporting differs.
        public event Action<Track>? TrackFailed;

        public GaplessCoordinator(
            LibVLC libVLC,
            GaplessRingBuffer sharedRing,
            ILogger<GaplessCoordinator>? logger = null,
            ILogger<TrackDecoder>? trackDecoderLogger = null,
            int stagingCapacityBytes = DefaultStagingCapacityBytes)
            : this(sharedRing, (track, ring) => new TrackDecoder(libVLC, track, ring, trackDecoderLogger), logger, stagingCapacityBytes)
        {
            _secondCore = new LibVLC();
            // The armed role's core is a second, independent LibVLC (see this
            // class's remarks), so it needs the dialog handlers set on it too -
            // it is the one that opens the *next* track's URL, which is exactly
            // where a certificate question would appear and stall a handover.
            VlcCertificateDialogs.AnswerUnattended(_secondCore);
            _cores = [libVLC, _secondCore];
            _currentDecoderFactory = (track, ring) => new TrackDecoder(_cores[_currentCoreIndex], track, ring, trackDecoderLogger);
            _armedDecoderFactory = (track, ring) => new TrackDecoder(_cores[1 - _currentCoreIndex], track, ring, trackDecoderLogger);
        }

        // Lets tests substitute a fake ITrackDecoder to exercise this
        // class's handover/idempotency/generation logic without touching
        // real LibVLC decode. Current and armed share the same factory here
        // - there's no real core contention to isolate in the fake path.
        public GaplessCoordinator(
            GaplessRingBuffer sharedRing,
            Func<Track, GaplessRingBuffer, ITrackDecoder> decoderFactory,
            ILogger<GaplessCoordinator>? logger = null,
            int stagingCapacityBytes = DefaultStagingCapacityBytes)
        {
            _stagingCapacityBytes = stagingCapacityBytes;
            _sharedRing = sharedRing;
            _currentDecoderFactory = decoderFactory;
            _armedDecoderFactory = decoderFactory;
            _logger = logger;
        }

        public Track? CurrentTrack
        {
            get
            {
                lock (_gate)
                    return _current?.Track;
            }
        }

        public long CurrentTrackBytesProduced
        {
            get
            {
                lock (_gate)
                    return _current == null ? 0 : Math.Max(0, _sharedRing.TotalBytesRead - _currentTrackReadSplit);
            }
        }

        // Starts track fresh unless it's already the one that just became
        // current via a natural gapless handover (see class remarks) - in
        // that case this is a no-op, since restarting it would reintroduce
        // exactly the gap gapless is meant to remove.
        public void Play(Track track)
        {
            lock (_gate)
            {
                if (_current != null && _currentPath == track.Path)
                {
                    _logger?.LogTrace("Play({Path}): no-op, already current", track.Path);
                    return;
                }

                _logger?.LogInformation("Play({Path}): hard-flush from {PreviousPath}", track.Path, _currentPath);

                unchecked
                {
                    _generation++;
                }

                ClearArmedSlot(retireDecoder: true);

                _current?.Retire();
                _sharedRing.Reset();

                // Hard reset always lands "current" back on core 0
                // deterministically, matching a fresh Play() discarding
                // everything armed - see the dual-core remarks above.
                _currentCoreIndex = 0;

                var decoder = _currentDecoderFactory(track, _sharedRing);
                decoder.Drained += () => HandleDrainedOrFaulted(decoder, faulted: false);
                decoder.Faulted += () => HandleDrainedOrFaulted(decoder, faulted: true);
                decoder.SeekSettled += landedBytes => HandleSeekSettled(decoder, landedBytes);
                _current = decoder;
                _currentPath = track.Path;
                _currentTrackReadSplit = 0;

                decoder.StartDecoding();
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                unchecked
                {
                    _generation++;
                }

                ClearArmedSlot(retireDecoder: true);
                _current?.Retire();
                _current = null;
                _currentPath = null;
                _sharedRing.Reset();
            }
        }

        // Arms the given track to decode ahead in the background so it's
        // ready to splice in the instant the current track's decode ends.
        // next == null clears the armed slot (e.g. end of playlist with
        // repeat off). Safe to call repeatedly as shuffle/repeat state
        // changes mid-track - re-arming is a no-op if the same track is
        // already armed.
        public void SetUpcoming(Track? next)
        {
            lock (_gate)
            {
                if (next == null)
                {
                    ClearArmedSlot(retireDecoder: true);
                    return;
                }

                if (_armedTrack != null && _armedTrack.Path == next.Path)
                    return;

                _logger?.LogTrace("SetUpcoming({Path})", next.Path);

                ClearArmedSlot(retireDecoder: true);

                var stagingRing = new GaplessRingBuffer(_stagingCapacityBytes);
                var decoder = _armedDecoderFactory(next, stagingRing);
                _stagingRing = stagingRing;
                _armed = decoder;
                _armedTrack = next;

                var generation = _generation;
                _ = ArmAsync(decoder, generation);
            }
        }

        public void Seek(float position)
        {
            _logger?.LogDebug("Seek({Position}) on {Path}", position, _currentPath);
            lock (_gate)
            {
                if (_current != null)
                {
                    // The seek's flush/reset of the shared ring happens
                    // asynchronously - on the real decoder, sometime after
                    // _current.Seek(position) below, once LibVLC's own seek
                    // machinery gets around to it - so TotalBytesRead can't
                    // be read as "the fresh post-seek value" synchronously
                    // here. Instead, pre-negate the split by the seek
                    // target: once the flush lands and TotalBytesRead
                    // starts counting from 0 again in the new ring
                    // generation, CurrentTrackBytesProduced's subtraction
                    // (0 - -targetBytes) already reads as targetBytes
                    // immediately, then grows from there as playback
                    // resumes, without waiting on the decoder to report
                    // anything about the seek itself.
                    //
                    // That target is a provisional answer, not the final
                    // one: a lossy stream is seeked to a frame/keyframe
                    // boundary, so LibVLC routinely lands somewhere near
                    // the request rather than on it, and nothing here can
                    // know where. HandleSeekSettled re-anchors the split
                    // onto the real landing point once the decoder reports
                    // it (ITrackDecoder.SeekSettled), which is what stops
                    // the scrubber drifting away from the audio across
                    // repeated seeks.
                    var targetSeconds = _current.Track.Duration.TotalSeconds * position;
                    var targetBytes = (long)(targetSeconds * GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);
                    _currentTrackReadSplit = -targetBytes;
                }

                _current?.Seek(position);
            }
        }

        // Re-anchors the elapsed-time baseline onto where a seek actually
        // landed, replacing the requested target Seek() published
        // provisionally - see its remarks.
        //
        // landedBytes is the offset into the track of the first sample
        // decoded after the seek's flush, so the split is a flat
        // -landedBytes for exactly the reason Seek() pre-negates by the
        // target: that flush is what resets the ring's generation, and
        // TotalBytesRead counts from zero at the same sample landedBytes
        // describes. Anything the sink has already drained by the time
        // this arrives is real playback past the landing point and has to
        // keep counting - re-baselining against TotalBytesRead here would
        // silently discard it.
        //
        // Ignored for anything that is no longer current: a Play() or a
        // natural handover in between has already set its own split, and a
        // late settle from the decoder it replaced must not stomp it.
        private void HandleSeekSettled(ITrackDecoder decoder, long landedBytes)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_current, decoder))
                {
                    _logger?.LogTrace("HandleSeekSettled for {Path}: stale decoder, ignoring", decoder.Track.Path);
                    return;
                }

                _currentTrackReadSplit = -landedBytes;
                _logger?.LogTrace(
                    "Seek settled on {Path} at {LandedBytes} bytes - split re-anchored to {Split}",
                    _currentPath, landedBytes, _currentTrackReadSplit);
            }
        }

        private async Task ArmAsync(ITrackDecoder decoder, int generation)
        {
            bool prepared;
            // Kept so the reason survives to the Warning further down: a bare
            // catch here discarded it entirely, leaving "Decode-ahead prepare
            // failed" as the only trace of a track that will not play - a
            // failure with no cause attached, which is the hardest possible
            // shape to diagnose from a log somebody mailed in.
            Exception? prepareFailure = null;
            try
            {
                prepared = await decoder.PrepareAsync();
            }
            catch (Exception ex)
            {
                prepareFailure = ex;
                prepared = false;
            }

            // A short/fast-decoding current track can drain and hand over
            // before this method even finishes awaiting PrepareAsync above -
            // decoder is then already promoted to _current (and _armed
            // already cleared) by the time control gets back here. Found via
            // GaplessCoordinatorRealDecodeTests: playback silently stalled
            // forever because the old !ReferenceEquals(_armed, decoder)
            // check treated that as "superseded, nothing to do", so
            // StartDecoding() - which only ever gets called from this method
            // - never happened at all for the new current decoder.
            var promotedWhilePreparingAndFailed = false;

            lock (_gate)
            {
                if (generation != _generation)
                    return;

                var stillArmed = ReferenceEquals(_armed, decoder);
                var promotedWhilePreparing = !stillArmed && ReferenceEquals(_current, decoder);

                if (!stillArmed && !promotedWhilePreparing)
                    return;

                if (!prepared)
                {
                    if (stillArmed)
                    {
                        _logger?.LogWarning(prepareFailure, "Decode-ahead prepare failed for {Path} - clearing armed slot", decoder.Track.Path);
                        ClearArmedSlot(retireDecoder: true);
                        return;
                    }

                    // Promoted to current before its own prepare finished,
                    // and prepare then failed - has to be reported like any
                    // other current-decoder failure, but HandleDrainedOrFaulted
                    // can't be called from inside this lock (see its own
                    // remarks on why), so just flag it and handle it below.
                    _logger?.LogWarning(prepareFailure,
                        "Decode-ahead prepare failed for {Path} after it was already promoted to current - "
                        + "reporting it as a playback failure", decoder.Track.Path);
                    promotedWhilePreparingAndFailed = true;
                }
                else
                {
                    if (stillArmed)
                    {
                        decoder.Drained += () => HandleArmedDrained(decoder);
                        decoder.Faulted += () => HandleArmedFaulted(decoder);
                    }
                    else
                    {
                        _logger?.LogInformation("{Path} was promoted to current before its own decode-ahead prepare finished - starting it now", decoder.Track.Path);
                    }

                    decoder.StartDecoding();
                }
            }

            if (promotedWhilePreparingAndFailed)
                HandleDrainedOrFaulted(decoder, faulted: true);
        }

        private void HandleArmedDrained(ITrackDecoder decoder)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_armed, decoder))
                    return;

                _logger?.LogInformation("Armed track {Path} finished decoding before the current track did", decoder.Track.Path);
                _armedAlreadyDrained = true;
            }
        }

        private void HandleArmedFaulted(ITrackDecoder decoder)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_armed, decoder))
                    return;

                ClearArmedSlot(retireDecoder: true);
            }
        }

        // Shared handler for both "decode finished normally" and "decode
        // errored out" on the current decoder - in both cases the track is
        // done as far as playback is concerned, so the follow-up behavior
        // (promote the armed track if it's ready, otherwise stop) is the
        // same either way.
        //
        // What is NOT the same either way is what gets reported: `faulted`
        // decides whether this raises TrackFailed or EndReached. Both used to
        // funnel into EndReached, which is what PlaylistControlViewModel
        // increments a play count on - so an unplayable file skipped past
        // silently *and* counted as listened-to. See the two events' own
        // remarks.
        private void HandleDrainedOrFaulted(ITrackDecoder decoder, bool faulted)
        {
            Track finishedTrack;
            ITrackDecoder? promoted;
            var promotedAlreadyDrained = false;

            lock (_gate)
            {
                if (!ReferenceEquals(_current, decoder))
                {
                    _logger?.LogTrace("HandleDrainedOrFaulted for {Path}: stale decoder, ignoring", decoder.Track.Path);
                    return;
                }

                finishedTrack = decoder.Track;

                if (_armed != null && _stagingRing != null)
                {
                    promoted = _armed;
                    _current = promoted;
                    _currentPath = promoted.Track.Path;

                    // The write-index boundary between the finishing
                    // track's audio and the promoted one's - captured now,
                    // before PromoteTarget (below, outside this lock)
                    // appends any of the promoted decoder's already-decoded
                    // backlog. See _currentTrackReadSplit's remarks for why
                    // this replaces the old decoder-BytesProduced-based
                    // baseline.
                    _currentTrackReadSplit = _sharedRing.TotalBytesWritten;
                    promotedAlreadyDrained = _armedAlreadyDrained;

                    // The promoted decoder was created via
                    // _armedDecoderFactory, which always targets the OTHER
                    // core from whatever _currentCoreIndex was at the time -
                    // flipping it here keeps that invariant true for
                    // whatever gets armed next (see dual-core remarks above).
                    _currentCoreIndex = 1 - _currentCoreIndex;

                    // Wired again here (harmless no-op if it never fires,
                    // e.g. when promotedAlreadyDrained is true below) for
                    // the normal case: a promoted decoder that's still
                    // actively decoding needs its own eventual natural end
                    // to keep driving the chain forward, and nothing but
                    // this subscribes to it once it's no longer "armed".
                    promoted.Drained += () => HandleDrainedOrFaulted(promoted, faulted: false);
                    promoted.Faulted += () => HandleDrainedOrFaulted(promoted, faulted: true);

                    // Only now: an armed decoder is never seeked, so this
                    // is the first point at which the promoted one can be
                    // asked to.
                    promoted.SeekSettled += landedBytes => HandleSeekSettled(promoted, landedBytes);

                    ClearArmedSlot(retireDecoder: false);
                    _logger?.LogInformation("{Finished} drained - promoting armed {Next}", finishedTrack.Path, promoted.Track.Path);
                }
                else
                {
                    promoted = null;
                    _current = null;
                    _currentPath = null;
                    _logger?.LogInformation("{Finished} drained - nothing armed, stopping", finishedTrack.Path);
                }
            }

            if (faulted)
                TrackFailed?.Invoke(finishedTrack);
            else
                EndReached?.Invoke(finishedTrack);

            // Retire + the promoted decoder's staged-audio drain happen
            // outside _gate: PromoteTarget's Write() calls are paced by the
            // shared ring's real-time playback backpressure (bounded by
            // however much decode-ahead managed to buffer, up to
            // DefaultStagingCapacityBytes - tens of seconds), so holding _gate for
            // it would freeze every other coordinator call for just as long,
            // including the UI thread itself synchronously blocked inside
            // the Dispatcher-posted Play() from PlaylistControlViewModel's
            // own EndReached handler above. _current/_currentPath are
            // already updated, so a concurrent manual Play()/Seek() sees the
            // right decoder immediately; if a manual skip races in and
            // Retire()s/Resets the shared ring while this drain is still
            // running, GaplessRingBuffer's own generation check makes the
            // rest of the drain a harmless no-op rather than corrupting
            // anything.
            decoder.Retire();

            if (promoted != null)
            {
                promoted.PromoteTarget(_sharedRing);

                // The just-promoted decoder already reached Drained while
                // it was still armed (see _armedAlreadyDrained's remarks) -
                // its own Drained event fired once already, with nobody
                // listening, and a decoder never drains twice, so nothing
                // will ever call this again for it unless we do it
                // ourselves right now. Recursing here (rather than looping)
                // correctly cascades through any number of already-finished
                // tracks queued back to back. Found via
                // GaplessCoordinatorRealDecodeTests, where a 1-second armed
                // track reliably finished decoding before its 1-second
                // "current" track did.
                if (promotedAlreadyDrained)
                {
                    _logger?.LogInformation("{Path} had already finished decoding while armed - handling its completion immediately", promoted.Track.Path);
                    HandleDrainedOrFaulted(promoted, faulted: false);
                }
            }
        }

        // Must be called under _gate.
        private void ClearArmedSlot(bool retireDecoder)
        {
            if (retireDecoder)
                _armed?.Retire();

            _armed = null;
            _armedTrack = null;
            _stagingRing = null;
            _armedAlreadyDrained = false;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                ClearArmedSlot(retireDecoder: true);
                _current?.Retire();
                _current = null;
            }

            _secondCore?.Dispose();
        }
    }
}
