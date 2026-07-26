using System;
using System.Diagnostics;
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
        private const int StagingCapacityBytes = 60 * (int)GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame;

        private readonly GaplessRingBuffer _sharedRing;
        private readonly Func<Track, GaplessRingBuffer, ITrackDecoder> _decoderFactory;
        private readonly object _gate = new();

        private ITrackDecoder? _current;
        private string? _currentPath;

        private ITrackDecoder? _armed;
        private Track? _armedTrack;
        private GaplessRingBuffer? _stagingRing;

        private int _generation;

        // Diagnostic-only, temporary: null in every unit test (the
        // (GaplessRingBuffer, factory) constructor never passes one), so
        // every call below goes through _logger?. to stay a no-op there.
        private readonly ILogger<GaplessCoordinator>? _logger;

        // Fired once per track, when its decode is exhausted (or it faulted
        // mid-decode) - see class remarks.
        public event Action<Track>? EndReached;

        public GaplessCoordinator(
            LibVLC libVLC,
            GaplessRingBuffer sharedRing,
            ILogger<GaplessCoordinator>? logger = null,
            ILogger<TrackDecoder>? trackDecoderLogger = null)
            : this(sharedRing, (track, ring) => new TrackDecoder(libVLC, track, ring, trackDecoderLogger), logger)
        {
        }

        // Lets tests substitute a fake ITrackDecoder to exercise this
        // class's handover/idempotency/generation logic without touching
        // real LibVLC decode.
        public GaplessCoordinator(GaplessRingBuffer sharedRing, Func<Track, GaplessRingBuffer, ITrackDecoder> decoderFactory, ILogger<GaplessCoordinator>? logger = null)
        {
            _sharedRing = sharedRing;
            _decoderFactory = decoderFactory;
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
                    return _current?.BytesProduced ?? 0;
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
                    _logger?.LogInformation("Play({Path}): no-op, already current", track.Path);
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

                var decoder = _decoderFactory(track, _sharedRing);
                decoder.Drained += () => HandleDrainedOrFaulted(decoder);
                decoder.Faulted += () => HandleDrainedOrFaulted(decoder);
                _current = decoder;
                _currentPath = track.Path;

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

                _logger?.LogInformation("SetUpcoming({Path})", next.Path);

                ClearArmedSlot(retireDecoder: true);

                var stagingRing = new GaplessRingBuffer(StagingCapacityBytes);
                var decoder = _decoderFactory(next, stagingRing);
                _stagingRing = stagingRing;
                _armed = decoder;
                _armedTrack = next;

                var generation = _generation;
                _ = ArmAsync(decoder, generation);
            }
        }

        public void Seek(float position)
        {
            _logger?.LogInformation("Seek({Position}) on {Path}", position, _currentPath);
            lock (_gate)
                _current?.Seek(position);
        }

        private async Task ArmAsync(ITrackDecoder decoder, int generation)
        {
            bool prepared;
            try
            {
                prepared = await decoder.PrepareAsync();
            }
            catch
            {
                prepared = false;
            }

            lock (_gate)
            {
                if (generation != _generation || !ReferenceEquals(_armed, decoder))
                    return;

                if (!prepared)
                {
                    _logger?.LogWarning("Decode-ahead prepare failed for {Path} - clearing armed slot", decoder.Track.Path);
                    ClearArmedSlot(retireDecoder: true);
                    return;
                }

                decoder.Faulted += () => HandleArmedFaulted(decoder);
                decoder.StartDecoding();
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
        private void HandleDrainedOrFaulted(ITrackDecoder decoder)
        {
            Track finishedTrack;
            ITrackDecoder? promoted;

            lock (_gate)
            {
                if (!ReferenceEquals(_current, decoder))
                {
                    _logger?.LogDebug("HandleDrainedOrFaulted for {Path}: stale decoder, ignoring", decoder.Track.Path);
                    return;
                }

                finishedTrack = decoder.Track;

                if (_armed != null && _stagingRing != null)
                {
                    promoted = _armed;
                    _current = promoted;
                    _currentPath = promoted.Track.Path;
                    _armed = null;
                    _armedTrack = null;
                    _stagingRing = null;
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

            EndReached?.Invoke(finishedTrack);

            // Retire + the promoted decoder's staged-audio drain happen
            // outside _gate: PromoteTarget's Write() calls are paced by the
            // shared ring's real-time playback backpressure (bounded by
            // however much decode-ahead managed to buffer, up to
            // StagingCapacityBytes - tens of seconds), so holding _gate for
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
                var stopwatch = Stopwatch.StartNew();
                promoted.PromoteTarget(_sharedRing);
                _logger?.LogInformation("PromoteTarget for {Path} took {ElapsedMs}ms", promoted.Track.Path, stopwatch.ElapsedMilliseconds);
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
        }

        public void Dispose()
        {
            lock (_gate)
            {
                ClearArmedSlot(retireDecoder: true);
                _current?.Retire();
                _current = null;
            }
        }
    }
}
