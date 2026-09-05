using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using LibVLCSharp.Shared;

using Flower.Audio.Ffmpeg;
using Flower.Diagnostics;
using Flower.Logging;
using Flower.Models;

namespace Flower.Audio
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
    // EndReached fires at exactly the same moment/meaning the former VlcAudioManager's
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
        public static int DefaultStagingCapacityBytes =>
            60 * (int)GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame;

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

        // Lock-free mirrors of _current?.Track and _currentTrackReadSplit, for
        // the UI's position poll - see CurrentTrack/CurrentTrackBytesProduced.
        // Only ever written by the same code that writes the fields they
        // mirror, always under _gate, via PublishCurrent().
        private volatile Track? _publishedCurrentTrack;
        private long _publishedReadSplit;

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

        // Compared by LogDiagnosticSnapshot to detect a render path that says
        // it is running while consuming no new PCM across two snapshots.
        private string? _diagnosticLastPath;
        private int _diagnosticLastRingGeneration = -1;
        private long _diagnosticLastBytesRead = -1;

        // What the process is costing, logged beside the decode counters
        // rather than on its own line so the two can be read together. A phone
        // that is hot and stuttering is a question about both at once - the
        // interesting answer is "CPU pinned while the ring stayed full", which
        // neither half says alone. Its own monitor because CpuPercent is a
        // delta against whoever last sampled, and the settings screen polls the
        // same numbers on a different cadence.
        private readonly ResourceMonitor _resources = new();

        // Null in callers that deliberately do not request diagnostics, so
        // every call below remains a no-op there.
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
            ILogger<VlcDiagnosticLog>? vlcLogger = null,
            int stagingCapacityBytes = 0,
            TrackDecoderKind decoderKind = TrackDecoderKind.LibVlc,
            ILogger<FfmpegTrackDecoder>? ffmpegDecoderLogger = null)
            : this(sharedRing, (track, ring) => new TrackDecoder(libVLC, track, ring, trackDecoderLogger), logger, stagingCapacityBytes)
        {
            if (decoderKind == TrackDecoderKind.Ffmpeg)
            {
                // No second core, and none of what it is for. The contention
                // documented in this class's remarks is between two
                // SetAudioCallbacks MediaPlayers on one LibVLC core; an
                // FfmpegTrackDecoder owns a plain AVFormatContext and a thread
                // of its own, so current and armed share nothing at all and
                // there is nothing to isolate them from. The LibVLC handed in
                // is still the process's, still used by everything else that
                // wants one - it simply does no decoding this session.
                _currentDecoderFactory = (track, ring) => new FfmpegTrackDecoder(track, ring, ffmpegDecoderLogger);
                _armedDecoderFactory = _currentDecoderFactory;
                logger?.LogInformation("Decoding through flower-ffmpeg at {Format}", GaplessFormat.SampleFormat);
                return;
            }

            _secondCore = new LibVLC();
            // The armed role's core is a second, independent LibVLC (see this
            // class's remarks), so it needs the dialog handlers set on it too -
            // it is the one that opens the *next* track's URL, which is exactly
            // where a certificate question would appear and stall a handover.
            // Same for its log: a handover that fails to open the next track
            // fails on this core, not the one App.axaml.cs attached.
            VlcCertificateDialogs.AnswerUnattended(_secondCore);
            VlcDiagnosticLog.Attach(_secondCore, vlcLogger);
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
            int stagingCapacityBytes = 0)
        {
            _stagingCapacityBytes = stagingCapacityBytes == 0 ? DefaultStagingCapacityBytes : stagingCapacityBytes;
            _sharedRing = sharedRing;
            _currentDecoderFactory = decoderFactory;
            _armedDecoderFactory = decoderFactory;
            _logger = logger;
        }

        // Both of these are read every 250ms by the position timer, on the UI
        // thread, and both used to take _gate - which is also held across
        // LogDiagnosticSnapshot's formatting and across the whole locked
        // section of HandleDrainedOrFaulted. So the scrubber stalled at every
        // track transition, waiting on a lock it had no business needing.
        //
        // Published instead into volatile/interlocked fields that the same
        // locked sections write, so a reader gets the last consistent value
        // without blocking. A momentarily stale value here is invisible: it is
        // one 250ms tick of a scrub bar.
        public Track? CurrentTrack => _publishedCurrentTrack;

        public long CurrentTrackBytesProduced
        {
            get
            {
                if (_publishedCurrentTrack == null)
                    return 0;

                return Math.Max(0, _sharedRing.TotalBytesRead - Interlocked.Read(ref _publishedReadSplit));
            }
        }

        // Called every ten seconds while the render sink is running. Debug
        // snapshots give a healthy baseline around an incident; warnings are
        // reserved for impossible/no-progress states so they remain visible
        // at the default log level without producing a line every second.
        public void LogDiagnosticSnapshot(bool renderStarted)
        {
            // Everything is read under the lock and formatted outside it. The
            // Debug line below is long and interpolated by the logging
            // pipeline; holding _gate across it put the render path's own
            // handover behind message formatting once every ten seconds.
            string? currentPath;
            long played;
            long currentDecoded;
            int sharedGeneration;
            long sharedRead;
            long sharedWritten;
            long sharedAvailable;
            long sharedUnderruns;
            string? armedPath;
            long armedDecoded;
            long stagingRead;
            long stagingWritten;
            long stagingAvailable;
            int stagingCapacity;
            int stagingGeneration;
            bool armedAlreadyDrained;
            bool noProgress;
            bool runningWithNoDecoder;

            lock (_gate)
            {
                sharedGeneration = _sharedRing.Generation;
                sharedRead = _sharedRing.TotalBytesRead;
                sharedWritten = _sharedRing.TotalBytesWritten;
                sharedAvailable = _sharedRing.AvailableBytes;
                sharedUnderruns = _sharedRing.UnderrunCount;
                currentPath = _current?.Track.Path;
                currentDecoded = _current?.BytesProduced ?? 0;
                played = _current == null ? 0 : Math.Max(0, sharedRead - _currentTrackReadSplit);
                armedPath = _armedTrack?.Path;
                armedDecoded = _armed?.BytesProduced ?? 0;
                armedAlreadyDrained = _armedAlreadyDrained;

                var staging = _stagingRing;
                stagingRead = staging?.TotalBytesRead ?? 0;
                stagingWritten = staging?.TotalBytesWritten ?? 0;
                stagingAvailable = staging?.AvailableBytes ?? 0;
                stagingCapacity = staging?.Capacity ?? 0;
                stagingGeneration = staging?.Generation ?? -1;

                var sameStream = currentPath != null
                    && currentPath == _diagnosticLastPath
                    && sharedGeneration == _diagnosticLastRingGeneration;
                noProgress = renderStarted && sameStream && sharedRead == _diagnosticLastBytesRead;
                runningWithNoDecoder = renderStarted && currentPath == null;

                _diagnosticLastPath = currentPath;
                _diagnosticLastRingGeneration = sharedGeneration;
                _diagnosticLastBytesRead = sharedRead;
            }

            if (noProgress)
            {
                _logger?.LogWarning(
                    "Playback made no PCM consumption progress for 10s: Path={Path} PlayedBytes={PlayedBytes} DecodedBytes={DecodedBytes} SharedRead={SharedRead} SharedWritten={SharedWritten} SharedAvailable={SharedAvailable}/{SharedCapacity} RingGeneration={RingGeneration}",
                    currentPath, played, currentDecoded, sharedRead, sharedWritten,
                    sharedAvailable, _sharedRing.Capacity, sharedGeneration);
            }
            else if (runningWithNoDecoder)
            {
                _logger?.LogWarning(
                    "Render sink is running with no current decoder: SharedRead={SharedRead} SharedWritten={SharedWritten} SharedAvailable={SharedAvailable}/{SharedCapacity} RingGeneration={RingGeneration}",
                    sharedRead, sharedWritten, sharedAvailable,
                    _sharedRing.Capacity, sharedGeneration);
            }

            var resources = _resources.Sample();

            _logger?.LogDebug(
                "Playback snapshot: RenderStarted={RenderStarted} Current={CurrentPath} PlayedBytes={PlayedBytes} DecodedBytes={DecodedBytes} SharedRead={SharedRead} SharedWritten={SharedWritten} SharedAvailable={SharedAvailable}/{SharedCapacity} SharedUnderruns={SharedUnderruns} RingGeneration={RingGeneration} Armed={ArmedPath} ArmedDecodedBytes={ArmedDecodedBytes} StagingRead={StagingRead} StagingWritten={StagingWritten} StagingAvailable={StagingAvailable}/{StagingCapacity} StagingGeneration={StagingGeneration} ArmedAlreadyDrained={ArmedAlreadyDrained} CpuPercent={CpuPercent} ProcessMemoryMb={ProcessMemoryMb} ManagedHeapMb={ManagedHeapMb} Gen0={Gen0} Gen1={Gen1} Gen2={Gen2} Threads={Threads}",
                renderStarted, currentPath, played, currentDecoded, sharedRead,
                sharedWritten, sharedAvailable, _sharedRing.Capacity,
                sharedUnderruns, sharedGeneration, armedPath,
                armedDecoded, stagingRead, stagingWritten, stagingAvailable,
                stagingCapacity, stagingGeneration, armedAlreadyDrained,
                resources.CpuPercent?.ToString("F1") ?? "n/a",
                resources.ProcessMemoryBytes / 1024 / 1024,
                resources.ManagedHeapBytes / 1024 / 1024,
                resources.Gen0Collections, resources.Gen1Collections,
                resources.Gen2Collections, resources.ThreadCount);
        }

        // Starts track fresh unless it's already the one that just became
        // current via a natural gapless handover (see class remarks) - in
        // that case this is a no-op, since restarting it would reintroduce
        // exactly the gap gapless is meant to remove.
        //
        // immediate says whether the caller is a user gesture or the queue
        // advancing on its own, and it decides the fate of whatever the
        // outgoing track still has buffered in the shared ring. EndReached
        // fires when a track's *decode* is exhausted, which is up to a full
        // ring (RingCapacityBytes, ~2s) before its last sample has actually
        // been heard, so a Reset() at that moment cuts the end off every
        // track that wasn't handed over gaplessly - the last one in a queue,
        // one with a saved resume position (deliberately never armed), or any
        // advance where shuffle/repeat changed the answer mid-track. That is
        // the "songs stop before they should" symptom.
        //
        // - immediate: false (auto-advance) - if the outgoing decoder has
        //   already finished and its tail is still in the ring, keep it: the
        //   new decoder appends after it exactly the way a promoted decoder
        //   does, and the split is the ring's current write position rather
        //   than 0, so the tail plays out and the new track's elapsed time
        //   still starts at zero. No waiting anywhere - the ring itself is the
        //   queue.
        // - immediate: true (Next/Previous/double-click) - flush at once. The
        //   sink fades across the resulting discontinuity, so the cut is
        //   inaudible rather than a click.
        public void Play(Track track, bool immediate = true)
        {
            lock (_gate)
            {
                // By identity, not by Path. A local track's Path is a stable
                // filename, so comparing paths worked for as long as that was
                // the only kind of track; a streamed one's Path is a signed
                // OpenSubsonic URL minted fresh - new nonce, new signature -
                // every time PlaylistControlViewModel.ResolveForPlaybackAsync
                // runs, and it runs once for the arm and again for the
                // auto-advance. So the two spellings of the same track never
                // compared equal off the LAN, and this guard - the one the
                // class remarks promise makes Play() idempotent against a
                // natural handover - never fired there.
                //
                // What that cost, from a real phone log: the armed decoder was
                // promoted, and 70ms later the auto-advance's Play() hard-
                // flushed it and re-opened the same track from zero. Sixty
                // seconds of decode-ahead and the network fetch behind it
                // thrown away, the track downloaded and decoded twice, and the
                // re-opened decoder left writing into a shared ring still full
                // of the promoted audio - which LibVLC answered with 1,153
                // "buffer too late: dropped" in 29 seconds, i.e. audible
                // dropouts, on a phone that was also getting hot.
                //
                // Track.Id survives Clone(), which is exactly why
                // ResolveForPlaybackAsync clones rather than rewriting Path.
                if (_current != null && _current.Track.Id == track.Id)
                {
                    _logger?.LogTrace("Play({Path}): no-op, already current", LogPath.Short(track.Path));
                    return;
                }

                // Only safe while nothing is still decoding into the ring: a
                // live decoder would interleave its output with the new one's.
                // A retired decoder's writes are dropped, but retirement is
                // asynchronous, so "current is already null" (it drained) is
                // the only condition worth taking this path for - and it is
                // exactly the case that loses tails today.
                var bufferedTail = _sharedRing.AvailableBytes;
                var preserveTail = !immediate && _current == null && bufferedTail > 0;

                if (preserveTail)
                {
                    _logger?.LogInformation(
                        "Play({Path}): appending after {BufferedMs}ms of {PreviousPath} still buffered",
                        track.Path, BytesToMilliseconds(bufferedTail), _currentPath);
                }
                else
                {
                    _logger?.LogInformation("Play({Path}): hard-flush from {PreviousPath}", LogPath.Short(track.Path), LogPath.Short(_currentPath));
                }

                unchecked
                {
                    _generation++;
                }

                ClearArmedSlot(retireDecoder: true);

                _current?.Retire();
                if (!preserveTail)
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

                // Same reasoning as the promotion branch of
                // HandleDrainedOrFaulted: the new track's audio begins at the
                // ring's current write cursor, not at zero.
                _currentTrackReadSplit = preserveTail ? _sharedRing.TotalBytesWritten : 0;
                PublishCurrent();

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
                PublishCurrent();
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

                // Identity again, and for the same reason as Play's guard
                // above: re-arming is meant to be a no-op when the same track
                // is already armed, and a streamed track arrives here with a
                // freshly signed Path every time. Shuffle/repeat changes
                // re-arm mid-track, so a Path comparison tore down a decoder
                // that was already several seconds into decoding ahead and
                // started its network fetch over, once per toggle.
                if (_armedTrack != null && _armedTrack.Id == next.Id)
                    return;

                _logger?.LogTrace("SetUpcoming({Path})", LogPath.Short(next.Path));

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
                    // Flushed here, synchronously, rather than left to
                    // LibVLC's own asynchronous OnFlush -> ResetTarget: until
                    // that arrives the ring still holds up to a full
                    // RingCapacityBytes of pre-seek audio, and the render
                    // callback happily plays all of it before the new
                    // position is heard. The later OnFlush reset is then a
                    // harmless second generation bump.
                    _sharedRing.Reset();

                    // The split can't be read back off the ring
                    // synchronously either - TotalBytesRead is 0 for the new
                    // generation, but the decoder hasn't landed anywhere yet.
                    // Instead, pre-negate the split by the seek
                    // target: once TotalBytesRead starts counting from 0
                    // again in the new ring generation,
                    // CurrentTrackBytesProduced's subtraction
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
                    PublishCurrent();
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
                    _logger?.LogTrace("HandleSeekSettled for {Path}: stale decoder, ignoring", LogPath.Short(decoder.Track.Path));
                    return;
                }

                _currentTrackReadSplit = -landedBytes;
                PublishCurrent();
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
            var prepareResult = DecodePrepareResult.Failed;
            try
            {
                prepareResult = await decoder.PrepareAsync();
                prepared = prepareResult == DecodePrepareResult.Ready;
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
                        // Retired underneath us is an ordinary skip or queue
                        // change, not a finding - the armed slot still has to
                        // go, but nobody needs telling about it.
                        if (prepareResult == DecodePrepareResult.Retired)
                            _logger?.LogTrace("Decode-ahead prepare for {Path} was retired - clearing armed slot", LogPath.Short(decoder.Track.Path));
                        else
                            _logger?.LogWarning(prepareFailure,
                                "Decode-ahead prepare failed for {Path} ({Reason}) - clearing armed slot, this track's handover will not be gapless",
                                LogPath.Short(decoder.Track.Path), prepareResult);

                        ClearArmedSlot(retireDecoder: true);
                        return;
                    }

                    // Promoted to current before its own prepare finished,
                    // and prepare then failed - has to be reported like any
                    // other current-decoder failure, but HandleDrainedOrFaulted
                    // can't be called from inside this lock (see its own
                    // remarks on why), so just flag it and handle it below.
                    _logger?.LogWarning(prepareFailure,
                        "Decode-ahead prepare failed for {Path} ({Reason}) after it was already promoted to current - "
                        + "reporting it as a playback failure", LogPath.Short(decoder.Track.Path), prepareResult);
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
                        _logger?.LogInformation("{Path} was promoted to current before its own decode-ahead prepare finished - starting it now", LogPath.Short(decoder.Track.Path));
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

                _logger?.LogInformation("Armed track {Path} finished decoding before the current track did", LogPath.Short(decoder.Track.Path));
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

            // Baseline for the seam verdict below, taken at the moment the old
            // decoder completed - the start of the window in which a gapless
            // handover can go audibly wrong.
            long underrunsAtCompletion = 0;
            long bufferedAtPromotion = 0;

            lock (_gate)
            {
                if (!ReferenceEquals(_current, decoder))
                {
                    _logger?.LogTrace("HandleDrainedOrFaulted for {Path}: stale decoder, ignoring", LogPath.Short(decoder.Track.Path));
                    return;
                }

                finishedTrack = decoder.Track;

                var playedBytes = Math.Max(0, _sharedRing.TotalBytesRead - _currentTrackReadSplit);
                var bufferedBytes = _sharedRing.AvailableBytes;
                underrunsAtCompletion = _sharedRing.UnderrunCount;
                bufferedAtPromotion = bufferedBytes;
                var expectedBytes = BytesForDuration(finishedTrack.Duration);
                var playedMs = BytesToMilliseconds(playedBytes);
                var decodedMs = BytesToMilliseconds(decoder.BytesProduced);
                var bufferedMs = BytesToMilliseconds(bufferedBytes);

                _logger?.LogInformation(
                    "Current decoder completed: Path={Path} Faulted={Faulted} TaggedDurationMs={TaggedDurationMs} PlayedMs={PlayedMs} DecodedMs={DecodedMs} SharedBufferedMs={BufferedMs} SharedRead={SharedRead} SharedWritten={SharedWritten} SharedAvailable={SharedAvailable}/{SharedCapacity} RingGeneration={RingGeneration} Armed={ArmedPath} ArmedDecodedBytes={ArmedDecodedBytes} ArmedAlreadyDrained={ArmedAlreadyDrained}",
                    finishedTrack.Path, faulted, finishedTrack.Duration.TotalMilliseconds,
                    playedMs, decodedMs, bufferedMs, _sharedRing.TotalBytesRead,
                    _sharedRing.TotalBytesWritten, bufferedBytes, _sharedRing.Capacity,
                    _sharedRing.Generation, _armedTrack?.Path, _armed?.BytesProduced ?? 0,
                    _armedAlreadyDrained);

                // A track that produced not one sample did not play, whatever
                // LibVLC calls it. It reports a clean end for this - no error,
                // no fault - and EndReached is a *finished* track: the play
                // count goes up, the resume position is cleared, and the queue
                // advances as if it had been listened to. So a track LibVLC
                // could not make sense of at all (see TrackDecoder.DemuxHintFor
                // for the way this was found: every AAC stream on iOS) went
                // past silently and counted, and the next one did the same, and
                // an album emptied itself in twenty seconds.
                //
                // Reclassified here rather than handled in the ViewModel
                // because "was any audio produced" is only knowable here, and
                // because TrackFailed already means exactly this: don't count
                // it, don't repeat it, tell the user.
                if (!faulted && decoder.BytesProduced == 0)
                {
                    _logger?.LogWarning(
                        "Decoder for {Path} ended without producing any audio; treating it as a failure rather than a finished track",
                        LogPath.Short(finishedTrack.Path));
                    faulted = true;
                }

                if (!faulted && expectedBytes > 0 && decoder.BytesProduced + BytesForDuration(TimeSpan.FromSeconds(2)) < expectedBytes)
                {
                    _logger?.LogWarning(
                        "Decoder ended materially before the tagged duration: Path={Path} TaggedDurationMs={TaggedDurationMs} DecodedMs={DecodedMs} MissingMs={MissingMs} Media may be truncated or LibVLC may have ended early",
                        finishedTrack.Path, finishedTrack.Duration.TotalMilliseconds,
                        decodedMs, finishedTrack.Duration.TotalMilliseconds - decodedMs);
                }

                if (_armed == null && bufferedBytes > BytesForDuration(TimeSpan.FromMilliseconds(100)))
                {
                    _logger?.LogWarning(
                        "Decoder completed with no armed successor while {BufferedMs}ms of PCM remains buffered; a stop or hard Play now can cut off the track tail: Path={Path} BufferedBytes={BufferedBytes}",
                        bufferedMs, finishedTrack.Path, bufferedBytes);
                }

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
                    PublishCurrent();
                    _logger?.LogInformation("{Finished} drained - promoting armed {Next}", LogPath.Short(finishedTrack.Path), LogPath.Short(promoted.Track.Path));
                }
                else
                {
                    promoted = null;
                    _current = null;
                    _currentPath = null;
                    PublishCurrent();
                    _logger?.LogInformation("{Finished} drained - nothing armed, stopping", LogPath.Short(finishedTrack.Path));
                }
            }

            // Before the events, not after: EndReached's subscriber
            // (PlaylistControlViewModel) runs right here on the LibVLC decode
            // callback thread and does real work - a play-count UPDATE under
            // Library's lock, a resume-position write, a walk of the queue -
            // and until it returns, nothing has put a single byte of the
            // promoted track in front of the render callback. The only thing
            // covering that window is whatever is left of the finishing
            // track's tail in the shared ring, and ReportHandoverSeam then
            // blames the splice for the underrun. Priming first closes the
            // seam whenever the ring has room; when it doesn't, the ring is
            // full of tail and the subscribers have all of it to run in.
            // Cheap and non-blocking either way - see
            // RetargetableRingWriter.PrimeTarget for why the full drain can't
            // simply move up here instead.
            var primeSplice = promoted?.PrimeTarget(_sharedRing);

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
                var splice = promoted.PromoteTarget(_sharedRing);

                // One handover, measured across both halves: the staged total
                // and elapsed time add up, and the first byte belongs to
                // whichever half actually moved it - the prime when it had
                // room, the drain otherwise.
                var prime = primeSplice ?? default;
                var seam = new PromotionSplice(
                    prime.MovedAnything ? prime.StagedBytes : splice.StagedBytes,
                    prime.BytesMoved + splice.BytesMoved,
                    prime.MovedAnything ? prime.MillisecondsToFirstByte : splice.MillisecondsToFirstByte,
                    prime.MovedAnything ? prime.DestinationUnderrunsAtFirstByte : splice.DestinationUnderrunsAtFirstByte,
                    prime.TotalMilliseconds + splice.TotalMilliseconds);

                ReportHandoverSeam(finishedTrack, promoted.Track, seam, underrunsAtCompletion, bufferedAtPromotion);

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
                    _logger?.LogInformation("{Path} had already finished decoding while armed - handling its completion immediately", LogPath.Short(promoted.Track.Path));
                    HandleDrainedOrFaulted(promoted, faulted: false);
                }
            }
        }

        // The verdict on one handover: did the shared ring run dry between the
        // old track's last byte and the new track's first?
        //
        // This is the only thing in the pipeline that answers whether gapless
        // actually worked, as opposed to whether the state machine took the
        // right path. Everything else here reports structure - which decoder
        // was promoted, how many bytes moved, what the ring indices were - and
        // all of it can look perfect while the listener hears a gap.
        //
        // The window is bounded by PromotionSplice (see it for why it stops at
        // the first byte rather than spanning all of PromoteTarget). Underruns
        // inside it are counted on the shared ring, which only the render
        // callback drains, so a non-zero delta means the callback asked for
        // PCM across the seam and got none - an audible gap.
        //
        // Not called under _gate: PromoteTarget runs outside it, and this
        // reads only the values it was handed plus the ring's own atomics.
        private void ReportHandoverSeam(
            Track finished,
            Track promoted,
            PromotionSplice splice,
            long underrunsAtCompletion,
            long bufferedAtPromotion)
        {
            if (!splice.MovedAnything)
            {
                // No first byte to time, so there is no seam measurement to
                // report - and the reason is worse than a gap: the armed
                // decoder staged nothing, so the promoted track starts from
                // an empty ring and plays only as fast as it can decode.
                _logger?.LogWarning(
                    "Handover moved no staged audio: {Finished} -> {Promoted} StagedBytes={StagedBytes} BufferedAtPromotion={BufferedBytes} TotalMs={TotalMs} The armed decoder produced nothing to hand over",
                    finished.Path, promoted.Path, splice.StagedBytes,
                    bufferedAtPromotion, splice.TotalMilliseconds);
                return;
            }

            var underruns = Math.Max(0, splice.DestinationUnderrunsAtFirstByte - underrunsAtCompletion);
            var bufferedMs = BytesToMilliseconds(bufferedAtPromotion);

            if (underruns > 0)
            {
                _logger?.LogWarning(
                    "Handover was not gapless: {Finished} -> {Promoted} Underruns={Underruns} MsToFirstByte={MsToFirstByte} BufferedAtPromotionMs={BufferedMs} StagedBytes={StagedBytes} MovedBytes={MovedBytes} The shared ring ran dry before the promoted track's first PCM landed",
                    finished.Path, promoted.Path, underruns,
                    splice.MillisecondsToFirstByte, bufferedMs,
                    splice.StagedBytes, splice.BytesMoved);
                return;
            }

            _logger?.LogDebug(
                "Handover was gapless: {Finished} -> {Promoted} MsToFirstByte={MsToFirstByte} BufferedAtPromotionMs={BufferedMs} StagedBytes={StagedBytes} MovedBytes={MovedBytes} TotalMs={TotalMs}",
                finished.Path, promoted.Path, splice.MillisecondsToFirstByte,
                bufferedMs, splice.StagedBytes, splice.BytesMoved,
                splice.TotalMilliseconds);
        }

        // Mirrors the two fields the UI's position poll reads, so it never has
        // to take _gate for them. Must be called under _gate, after every
        // change to _current or _currentTrackReadSplit.
        private void PublishCurrent()
        {
            _publishedCurrentTrack = _current?.Track;
            Interlocked.Exchange(ref _publishedReadSplit, _currentTrackReadSplit);
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

        private static long BytesForDuration(TimeSpan duration) =>
            (long)(duration.TotalSeconds * GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);

        private static double BytesToMilliseconds(long bytes) =>
            bytes / (double)(GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame) * 1000;

        public void Dispose()
        {
            lock (_gate)
            {
                ClearArmedSlot(retireDecoder: true);
                _current?.Retire();
                _current = null;
                PublishCurrent();
            }

            _secondCore?.Dispose();
        }
    }
}
