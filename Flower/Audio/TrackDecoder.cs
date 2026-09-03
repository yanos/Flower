using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using LibVLCSharp.Shared;

using Flower.Logging;
using Flower.Models;

namespace Flower.Audio
{
    // Wraps one decode-only LibVLC MediaPlayer: SetAudioFormat +
    // SetAudioCallbacks redirect its decoded PCM into a GaplessRingBuffer
    // instead of letting VLC's own aout render it. Used for both the
    // "currently playing" and "decode-ahead" roles in GaplessCoordinator -
    // each instance is single-use for one decode pass of one track.
    //
    // The decode-ahead role starts out writing into its own private staging
    // ring (so its output can't interleave with the still-playing current
    // track), then gets PromoteTarget()'d onto the shared ring at handover
    // time. RetargetableRingWriter makes "drain whatever's in the old
    // target, then start writing to the new one" atomic with respect to
    // this decoder's own producer thread, so no bytes can be produced into
    // a target that's mid-retarget and get lost or duplicated - see it for
    // why that is more than a plain lock around a blocking write.
    public sealed class TrackDecoder : ITrackDecoder
    {
        private readonly LibVLC _libVLC;
        private readonly MediaPlayer _mediaPlayer;
        private readonly RetargetableRingWriter _writer;

        // Cached because it is handed to the writer on every single audio
        // callback - see OnPlay.
        private readonly Func<bool> _isRetired;
        private Media? _media;
        private byte[] _scratch = [];
        private long _bytesProduced;
        private int _retired;

        // Guards the native Media/MediaPlayer against being disposed by Retire()
        // while PrepareAsync/StartDecoding is still using them - see Retire().
        private readonly SemaphoreSlim _nativeGate = new(1, 1);
        private volatile bool _drainFired;

        // Null in tests/callers that deliberately do not request diagnostics.
        private readonly ILogger<TrackDecoder>? _logger;

        // Watches this decode MediaPlayer's own
        // reported State/IsPlaying/BytesProduced once a second, so a
        // seek-induced wedge shows up here too - distinguishing "the decode
        // player itself thinks it's paused/stopped" from "it thinks it's
        // still playing but has simply stopped calling OnPlay". Only logs
        // when something looks wrong (stalled/mismatched/unexpected state);
        // a healthy decode ticks silently.
        // How long a prepare waits for the media to be parsed. Now that this
        // reaches the network (see PrepareAsync), it is also how long a
        // stopped or unreachable server takes to be reported as TimedOut. It
        // runs off the lock while the current track keeps playing, so the
        // cost of waiting is a late arm rather than a stall.
        private const int ParseTimeoutMs = 5000;

        private readonly System.Timers.Timer? _watchdog;
        private long _watchdogLastBytesProduced = -1;
        private long _watchdogLastBackpressureWaits = -1;
        private long _nextProgressLogBytes = BytesForSeconds(10);

        public Track Track { get; }

        // Bytes of canonical PCM decoded so far for this track, from
        // whatever position it started/last seeked from - used to compute
        // elapsed Time/Position without depending on the render sink, which
        // has no concept of track boundaries in its own stream/graph.
        public long BytesProduced => Interlocked.Read(ref _bytesProduced);

        // Raised once decode reaches the end of the track (LibVLC's drain
        // callback) - the authoritative "this track's samples are
        // exhausted" signal, independent of the higher-level
        // MediaPlayer.EndReached event.
        public event Action? Drained;

        // Raised if decode fails to start, fails to parse, or errors out
        // mid-decode.
        public event Action? Faulted;

        // See ITrackDecoder.SeekSettled. Resolved from the decode player's
        // own clock at the first sample delivered after the seek's flush,
        // which is the earliest moment LibVLC can report where it actually
        // landed - MediaPlayer.Position is set asynchronously, so reading
        // it back inside Seek() just returns the request.
        public event Action<long>? SeekSettled;

        // Seek() -> OnFlush -> OnPlay, as two one-shot handoffs. The flush
        // is what marks the boundary the landing offset is measured from
        // (it's the same boundary the coordinator's ring reset uses), and
        // OnPlay is the first moment the player's clock reflects the new
        // position. Two seeks in quick succession collapse into a single
        // resolution against whichever one the player settled on, which is
        // the right answer for both.
        private int _seekRequested;
        private int _seekAwaitingFirstSample;

        public TrackDecoder(LibVLC libVLC, Track track, GaplessRingBuffer initialTarget, ILogger<TrackDecoder>? logger = null)
        {
            _libVLC = libVLC;
            Track = track;
            _writer = new RetargetableRingWriter(initialTarget);
            _isRetired = () => Volatile.Read(ref _retired) == 1;
            _logger = logger;

            _mediaPlayer = new MediaPlayer(_libVLC);
            _mediaPlayer.EncounteredError += (_, _) =>
            {
                _logger?.LogWarning("Decode MediaPlayer for {Path} encountered an error", LogPath.Short(Track.Path));
                Faulted?.Invoke();
            };

            // OnDrain (the raw audio callback registered in StartDecoding)
            // is supposed to be the authoritative "samples exhausted"
            // signal, but a real repro showed it can simply never fire for
            // a track that otherwise reaches LibVLC's own higher-level
            // Ended state - GaplessCoordinator then has nothing armed that
            // will ever get promoted, and playback sits stuck at the end
            // forever. EndReached is a backstop for exactly that case: give
            // OnDrain a head start (it fires right at the last real sample,
            // where EndReached can fire once LibVLC's higher-level state
            // machine has settled, not necessarily at the same instant),
            // and only force the handover ourselves if OnDrain still hasn't
            // shown up. GaplessCoordinator.HandleDrainedOrFaulted already
            // no-ops a Drained call for a decoder that isn't _current
            // anymore, so firing this after a real OnDrain already handled
            // things is harmless.
            _mediaPlayer.EndReached += (_, _) =>
            {
                _logger?.LogTrace("EndReached (high-level) for {Path}", LogPath.Short(Track.Path));
                _ = FallbackDrainIfOnDrainNeverFiresAsync();
            };

            // Every LibVLC callback above logs at Trace, not Information: one
            // line per callback per track is a running commentary nobody reads
            // during normal playback, but it is exactly what is wanted when a
            // handover misbehaves - and this pipeline has had two subtle bugs
            // found by reading precisely this sequence (see GaplessCoordinator's
            // _secondCore remarks). So it is demoted rather than deleted, and
            // turning it back on is a level change, not a rebuild.
            //
            // The watchdog below stays at Warning: it is edge-triggered on an
            // actual anomaly rather than on every callback.
            if (_logger != null)
            {
                _watchdog = new System.Timers.Timer(1000);
                _watchdog.Elapsed += (_, _) => CheckWatchdog();
                _watchdog.Start();
            }
        }

        // Gives OnDrain a head start before treating EndReached as the
        // authoritative end-of-track signal instead - see the EndReached
        // subscription's comment above for why both exist.
        private async Task FallbackDrainIfOnDrainNeverFiresAsync()
        {
            await Task.Delay(500);

            if (Volatile.Read(ref _retired) == 1 || _drainFired)
                return;

            _logger?.LogWarning("OnDrain never fired for {Path} within 500ms of EndReached - forcing the handover from here instead", LogPath.Short(Track.Path));
            Drained?.Invoke();
        }

        // Parses the track up front so a bad/missing/unsupported file is
        // caught before it's ever promoted to "current" or spliced into the
        // shared ring buffer, letting the coordinator degrade to "nothing
        // armed" instead of glitching playback. Safe to skip for the
        // "currently playing" role, where the user just explicitly chose it.
        public async Task<DecodePrepareResult> PrepareAsync(CancellationToken cancellationToken = default)
        {
            // See Retire(): the gate keeps this method's native Media alive for
            // as long as Parse is using it, even if the coordinator retires this
            // decoder mid-parse.
            await _nativeGate.WaitAsync(cancellationToken);
            try
            {
                if (Volatile.Read(ref _retired) == 1)
                    return DecodePrepareResult.Retired;

                var media = EnsureMedia();

                // ParseNetwork, not ParseLocal. ParseLocal is documented as
                // "parse media if it's a local file", so a track streamed from
                // a server was skipped without a single network request and
                // came back not-Done - which the coordinator read as a failed
                // prepare and answered by clearing the armed slot. Decode-ahead
                // was therefore off for every streamed track, always, and the
                // gapless seam it exists to cover became an ordinary gap. One
                // device log: 32 tracks played, 32 prepare failures, zero
                // successful arms. ParseNetwork covers local media too (the
                // flag reads "parse media *even if* it's a network file"), so
                // there is one path here rather than a branch on the path.
                var status = await media.Parse(MediaParseOptions.ParseNetwork, ParseTimeoutMs, cancellationToken);

                return status switch
                {
                    MediaParsedStatus.Done => DecodePrepareResult.Ready,
                    MediaParsedStatus.Timeout => DecodePrepareResult.TimedOut,
                    MediaParsedStatus.Skipped => DecodePrepareResult.NotAttempted,
                    _ => DecodePrepareResult.Failed,
                };
            }
            finally
            {
                _nativeGate.Release();
            }
        }

        public void StartDecoding()
        {
            _logger?.LogTrace("StartDecoding() for {Path}", LogPath.Short(Track.Path));

            _nativeGate.Wait();
            try
            {
                if (Volatile.Read(ref _retired) == 1)
                    return;

                var media = EnsureMedia();
                _mediaPlayer.SetAudioFormat(GaplessFormat.LibVlcFourCc, GaplessFormat.SampleRate, GaplessFormat.Channels);
                _mediaPlayer.SetAudioCallbacks(OnPlay, OnPause, OnResume, OnFlush, OnDrain);

                if (!_mediaPlayer.Play(media))
                    Faulted?.Invoke();
            }
            finally
            {
                _nativeGate.Release();
            }
        }

        // Only logs when something looks wrong, so a healthy decode doesn't
        // spam once-a-second lines for the whole track: State claiming
        // Playing while IsPlaying disagrees, State sitting somewhere it
        // shouldn't for a still-live decoder, or BytesProduced not moving
        // despite the player believing it's actively playing (the seek-
        // induced wedge this watchdog exists to catch).
        private void CheckWatchdog()
        {
            var state = _mediaPlayer.State;
            var isPlaying = _mediaPlayer.IsPlaying;
            var bytesProduced = BytesProduced;
            var target = _writer.Target;

            // A decoder that has filled its ring and is waiting for playback
            // to drain it produces no bytes either, and that is the healthy
            // steady state for most of a track, not a wedge. Without this the
            // watchdog cried stall on every decode-ahead: a real device logged
            // 2430 of these in a day, every one of them with the ring at
            // exactly 384000/384000 and nothing whatsoever wrong.
            //
            // The signal is the writer's own parked-for-room count rather than
            // free space here, because a snapshot of free space is taken at an
            // arbitrary instant - the reader drains a period every few
            // milliseconds, so a full ring reads as briefly not-full - whereas
            // "did it park at any point during this window" is exactly the
            // question and cannot be lost to sampling.
            var backpressureWaits = _writer.BackpressureWaits;

            var stalled = IsStalled(
                state, isPlaying,
                bytesProduced, _watchdogLastBytesProduced,
                backpressureWaits, _watchdogLastBackpressureWaits);
            var stateMismatch = state == VLCState.Playing && !isPlaying;
            var unexpectedState = Volatile.Read(ref _retired) == 0
                && state is VLCState.Error or VLCState.Stopped;

            if (stalled || stateMismatch || unexpectedState)
            {
                _logger?.LogWarning(
                    "Decode watchdog for {Path}: State={State} IsPlaying={IsPlaying} Time={Time}ms BytesProduced={BytesProduced} TargetRead={TargetRead} TargetWritten={TargetWritten} TargetAvailable={TargetAvailable}/{TargetCapacity} TargetGeneration={TargetGeneration} (Stalled={Stalled} StateMismatch={StateMismatch} UnexpectedState={UnexpectedState})",
                    LogPath.Short(Track.Path), state, isPlaying, _mediaPlayer.Time, bytesProduced,
                    target.TotalBytesRead, target.TotalBytesWritten, target.AvailableBytes,
                    target.Capacity, target.Generation, stalled, stateMismatch, unexpectedState);
            }

            _watchdogLastBytesProduced = bytesProduced;
            _watchdogLastBackpressureWaits = backpressureWaits;
        }

        // Pulled out of CheckWatchdog so it can be tested: the surrounding
        // method can only be driven through a real MediaPlayer, and this
        // predicate getting it wrong is what produced 2430 false alarms a day.
        // The two "last" arguments are the previous tick's samples, -1 on the
        // first tick, where nothing can be concluded from a delta yet.
        internal static bool IsStalled(
            VLCState state,
            bool isPlaying,
            long bytesProduced,
            long lastBytesProduced,
            long backpressureWaits,
            long lastBackpressureWaits)
        {
            if (state != VLCState.Playing || !isPlaying)
                return false;

            // First tick: no previous sample of either counter, so there is no
            // delta to conclude anything from. The watchdog runs once a
            // second and there is always a next one.
            if (lastBytesProduced < 0)
                return false;

            if (bytesProduced != lastBytesProduced)
                return false;

            var waitedForRoom = lastBackpressureWaits >= 0 && backpressureWaits != lastBackpressureWaits;
            return !waitedForRoom;
        }

        // Seeks this decoder's own demux/decode to the given position
        // (0..1) and resets the byte-produced counter to match, so
        // Time/Position stay correct across the seek. The counter is set
        // to the *requested* offset here and corrected to the offset the
        // demuxer actually landed on once the seek settles - see
        // ResolveSeekLanding and SeekSettled. LibVLC's own flush
        // callback (OnFlush) fires as a side effect, discarding whatever
        // pre-seek audio was already sitting in this decoder's current
        // target ring.
        public void Seek(float position)
        {
            _logger?.LogTrace(
                "Seek({Position}) on {Path}: State={State} IsPlaying={IsPlaying} before seek",
                position, Track.Path, _mediaPlayer.State, _mediaPlayer.IsPlaying);
            Interlocked.Exchange(ref _seekRequested, 1);
            _mediaPlayer.Position = position;
            Interlocked.Exchange(ref _bytesProduced, BytesForSeconds(Track.Duration.TotalSeconds * position));

            // Defensive: if LibVLC's seek sequence internally pauses the
            // decode pipeline as part of repositioning and never resumes it
            // (suspected, pending confirmation from the OnPause/OnResume
            // logging above and the watchdog), this nudges it back to
            // playing. Harmless no-op if it's already playing.
            _mediaPlayer.SetPause(false);
        }

        // Moves whatever fits into newTarget right now, without blocking and
        // without switching the write target - see
        // RetargetableRingWriter.PrimeTarget. Deliberately unlogged: it runs
        // in the seam's critical path and usually moves nothing, so
        // PromoteTarget's own logging below covers the handover.
        public PromotionSplice PrimeTarget(GaplessRingBuffer newTarget) =>
            _writer.PrimeTarget(newTarget);

        // Drains everything currently buffered in this decoder's target
        // ring into newTarget, then switches future output to newTarget -
        // see RetargetableRingWriter.
        public PromotionSplice PromoteTarget(GaplessRingBuffer newTarget)
        {
            var oldTarget = _writer.Target;
            var stagedBytes = oldTarget.AvailableBytes;
            _logger?.LogInformation(
                "Promoting decoder output for {Path}: StagedBytes={StagedBytes} StagingRead={StagingRead} StagingWritten={StagingWritten} StagingCapacity={StagingCapacity} StagingGeneration={StagingGeneration} DestinationAvailable={DestinationAvailable}/{DestinationCapacity} DestinationGeneration={DestinationGeneration}",
                Track.Path, stagedBytes, oldTarget.TotalBytesRead,
                oldTarget.TotalBytesWritten, oldTarget.Capacity, oldTarget.Generation,
                newTarget.AvailableBytes, newTarget.Capacity, newTarget.Generation);

            var splice = _writer.PromoteTarget(newTarget);

            // MovedBytes comes from the splice rather than the stagedBytes
            // snapshot above: that snapshot is taken before the gate is
            // taken, so the armed decoder can add to the staging ring in
            // between and the two legitimately differ.
            _logger?.LogInformation(
                "Decoder output promotion completed for {Path}: MovedBytes={MovedBytes} SnapshotStagedBytes={SnapshotStagedBytes} MsToFirstByte={MsToFirstByte} TotalMs={TotalMs} DestinationRead={DestinationRead} DestinationWritten={DestinationWritten} DestinationAvailable={DestinationAvailable}/{DestinationCapacity} DestinationGeneration={DestinationGeneration}",
                Track.Path, splice.BytesMoved, stagedBytes,
                splice.MillisecondsToFirstByte, splice.TotalMilliseconds,
                newTarget.TotalBytesRead, newTarget.TotalBytesWritten,
                newTarget.AvailableBytes, newTarget.Capacity, newTarget.Generation);

            return splice;
        }

        // Marks this decoder retired - its in-flight/late callbacks become
        // no-ops - then stops the underlying MediaPlayer. Used when
        // GaplessCoordinator abandons a decoder on manual skip/flush, or
        // once it's been fully superseded after a handover.
        //
        // The _retired flag flip above is what actually matters for
        // correctness (every OnPlay/OnFlush/OnDrain callback checks it and
        // no-ops immediately once set) - MediaPlayer.Stop() itself is a
        // synchronous native call that both of Retire()'s callers invoke
        // while depending on it returning promptly (GaplessCoordinator.Play
        // calls it on the UI thread while holding its own _gate lock;
        // HandleDrainedOrFaulted calls it from a LibVLC decode-callback
        // thread). LibVLCSharp's Stop() has a known footgun where it can
        // block for a long time - or hang outright - depending on the
        // player's internal state when called; a real repro (manual track
        // skip beachballing the whole UI) confirmed it's not safe to assume
        // this returns quickly. Running it on its own thread means a slow
        // or wedged Stop() only strands that one throwaway thread, never
        // the caller.
        public void Retire()
        {
            if (Interlocked.Exchange(ref _retired, 1) == 1)
                return;

            _logger?.LogTrace("Retire() for {Path}", LogPath.Short(Track.Path));
            _watchdog?.Stop();

            var path = Track.Path;
            var logger = _logger;
            _ = Task.Run(async () =>
            {
                // Serializes against PrepareAsync/StartDecoding: the coordinator
                // can retire a decoder whose PrepareAsync is still in flight
                // (see GaplessCoordinator.ArmAsync's own remarks on that race),
                // and disposing the native Media out from under an in-progress
                // Parse is a native crash, not an exception.
                await _nativeGate.WaitAsync();
                try
                {
                    _mediaPlayer.Stop();
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "MediaPlayer.Stop() failed during Retire() for {Path}", path);
                }

                try
                {
                    // The actual release of the native handles. This is a fresh
                    // Media + MediaPlayer per track, and nothing used to dispose
                    // them: GaplessCoordinator only ever calls Retire(), never
                    // Dispose(), so every track change leaked both for the life
                    // of the process. Done here rather than in Retire's body
                    // because it has to happen after Stop() returns, and Stop()
                    // is the call that can hang (see this method's remarks).
                    _watchdog?.Dispose();
                    _media?.Dispose();
                    _mediaPlayer.Dispose();
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Disposing native decode resources failed during Retire() for {Path}", path);
                }
                finally
                {
                    _nativeGate.Release();
                }
            });
        }

        private Media EnsureMedia()
        {
            // Track.Path is null for a sync placeholder (known via another
            // device's library but not yet downloaded locally) - decoding
            // one of those is a caller bug, not something to silently no-op.
            if (Track.Path is not { } path)
                throw new InvalidOperationException($"Cannot decode \"{Track.Title}\" - it has no local Path (undownloaded sync placeholder).");

            // Android's MediaStore importer hands back content:// URIs
            // rather than filesystem paths; those need FromLocation, not
            // the default FromPath.
            if (_media is not null)
                return _media;

            _media = path.Contains("://")
                ? new Media(_libVLC, path, FromType.FromLocation)
                : new Media(_libVLC, path);

            ApplyNetworkOptions(_media, path);
            return _media;
        }

        // How much of a remote track LibVLC reads ahead of what is playing.
        //
        // Its own default is one second, which is what a phone streaming from
        // a server over the open internet had to ride out every hiccup on -
        // a second of buffer plus the two seconds the shared PCM ring holds.
        // Anything longer than three seconds of trouble was audible, and a day
        // of logs where the network was having a bad time is a day of
        // starvation warnings.
        //
        // The cost is start latency: LibVLC fills this before the first sample
        // comes out, so a manually-started track takes correspondingly longer
        // to begin. Auto-advance does not pay it, because the next decoder is
        // armed and filling well before the current track ends. Ten seconds is
        // the compromise - long enough to be worth having, short enough that
        // pressing play still feels like pressing play.
        //
        // This is buffering, not caching: nothing is held on disk, and a track
        // is still fetched again the next time it is played. Keeping whole
        // tracks would be a download, which is a feature this pipeline does
        // not have (see LibraryDownloadService for the deliberate kind).
        private const int RemoteNetworkCachingMs = 10_000;

        private static void ApplyNetworkOptions(Media media, string path)
        {
            if (!path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            media.AddOption($":network-caching={RemoteNetworkCachingMs}");

            // A stream that drops mid-track is otherwise the end of that
            // track: LibVLC reports an error, the coordinator gives up, and
            // the row is skipped as unplayable. Reconnecting is the difference
            // between a gap and a song the listener never heard.
            media.AddOption(":http-reconnect");
        }

        private void OnPlay(IntPtr data, IntPtr samples, uint count, long pts)
        {
            if (Volatile.Read(ref _retired) == 1)
                return;

            // Before anything is written for this buffer, so the landing
            // offset lines up with the ring position this buffer starts at
            // rather than the one after it.
            if (Interlocked.Exchange(ref _seekAwaitingFirstSample, 0) == 1)
                ResolveSeekLanding();

            var byteCount = checked((int)count * GaplessFormat.BytesPerFrame);
            if (_scratch.Length < byteCount)
                _scratch = new byte[byteCount];

            Marshal.Copy(samples, _scratch, 0, byteCount);

            // The retire check is handed over as a callback rather than
            // being tested once up front: this can park for as long as the
            // target ring stays full (a whole track's worth, for a decoder
            // that filled its staging ring long before handover), and a
            // Retire() landing in the middle of that has to get out.
            _writer.Write(_scratch.AsSpan(0, byteCount), _isRetired);

            var bytesProduced = Interlocked.Add(ref _bytesProduced, byteCount);
            if (_logger != null && bytesProduced >= Volatile.Read(ref _nextProgressLogBytes))
            {
                var interval = BytesForSeconds(10);
                while (_nextProgressLogBytes <= bytesProduced)
                    _nextProgressLogBytes += interval;

                var target = _writer.Target;
                _logger.LogDebug(
                    "Decode progress: Path={Path} MediaTimeMs={MediaTimeMs} BytesProduced={BytesProduced} DecodedMs={DecodedMs} TargetRead={TargetRead} TargetWritten={TargetWritten} TargetAvailable={TargetAvailable}/{TargetCapacity} TargetGeneration={TargetGeneration}",
                    LogPath.Short(Track.Path), _mediaPlayer.Time, bytesProduced,
                    bytesProduced / (double)(GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame) * 1000,
                    target.TotalBytesRead, target.TotalBytesWritten, target.AvailableBytes,
                    target.Capacity, target.Generation);
            }
        }

        private void OnPause(IntPtr data, long pts)
        {
            _logger?.LogTrace("OnPause for {Path}", LogPath.Short(Track.Path));
        }

        private void OnResume(IntPtr data, long pts)
        {
            _logger?.LogTrace("OnResume for {Path}", LogPath.Short(Track.Path));
        }

        // LibVLC's aout flush. Only the *requested* kind empties the ring.
        //
        // A flush this decoder asked for - a seek - means everything buffered
        // is audio from somewhere the listener is no longer going to, so it has
        // to go. A flush nobody asked for means something reset LibVLC's audio
        // output underneath us: on iOS, an output-route change or the app
        // coming back from a long suspension does exactly that. The PCM already
        // in the ring is still this track, still contiguous, and still the next
        // thing due to be played - throwing it away turns a route change into a
        // guaranteed dropout.
        //
        // That is what a day of phone logs shows: a spontaneous flush arrived
        // 1.5s after a suspend/resume with 341,624 of 384,000 bytes buffered
        // (3.5s of audio, ready to play), reset the ring to empty, and LibVLC
        // then produced nothing at all for the next fifteen seconds - its own
        // clock had run on while the app was suspended, so every freshly
        // decoded buffer read as late and was dropped until the decode caught
        // up. Keeping the ring does not shorten that catch-up, but it does play
        // through the first three and a half seconds of it instead of
        // rendering silence from the first moment.
        private void OnFlush(IntPtr data, long pts)
        {
            if (Volatile.Read(ref _retired) == 1)
                return;

            var requested = Interlocked.Exchange(ref _seekRequested, 0) == 1;
            var target = _writer.Target;

            if (requested)
            {
                _logger?.LogInformation(
                    "Decode flush for {Path}: Pts={Pts} BytesProduced={BytesProduced} TargetRead={TargetRead} TargetWritten={TargetWritten} TargetAvailable={TargetAvailable}/{TargetCapacity} TargetGeneration={TargetGeneration}; seek requested it, resetting target ring",
                    Track.Path, pts, BytesProduced,
                    target.TotalBytesRead, target.TotalBytesWritten, target.AvailableBytes,
                    target.Capacity, target.Generation);

                Interlocked.Exchange(ref _seekAwaitingFirstSample, 1);
                _writer.ResetTarget();
                return;
            }

            _logger?.LogWarning(
                "Decode flush for {Path}: Pts={Pts} BytesProduced={BytesProduced} TargetRead={TargetRead} TargetWritten={TargetWritten} TargetAvailable={TargetAvailable}/{TargetCapacity} TargetGeneration={TargetGeneration}; nothing asked for it, keeping the buffered audio",
                Track.Path, pts, BytesProduced,
                target.TotalBytesRead, target.TotalBytesWritten, target.AvailableBytes,
                target.Capacity, target.Generation);
        }

        private void OnDrain(IntPtr data)
        {
            _drainFired = true;

            if (Volatile.Read(ref _retired) == 1)
                return;

            var target = _writer.Target;
            var decodedMs = BytesProduced / (double)(GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame) * 1000;
            _logger?.LogInformation(
                "Decode drain for {Path}: MediaTimeMs={MediaTimeMs} TaggedDurationMs={TaggedDurationMs} DecodedMs={DecodedMs} BytesProduced={BytesProduced} TargetRead={TargetRead} TargetWritten={TargetWritten} TargetAvailable={TargetAvailable}/{TargetCapacity} TargetGeneration={TargetGeneration}",
                Track.Path, _mediaPlayer.Time, Track.Duration.TotalMilliseconds,
                decodedMs, BytesProduced, target.TotalBytesRead, target.TotalBytesWritten,
                target.AvailableBytes, target.Capacity, target.Generation);
            Drained?.Invoke();
        }

        // Takes the decode player's own clock as the truth about where the
        // seek landed and republishes it. Time is in milliseconds and is
        // -1 when the player has no usable clock at all, in which case the
        // requested target Seek() already published stands - a stale
        // target is a better answer than a wrong one.
        private void ResolveSeekLanding()
        {
            var timeMs = _mediaPlayer.Time;
            if (timeMs < 0)
                return;

            var landedBytes = Math.Clamp(BytesForSeconds(timeMs / 1000.0), 0, BytesForSeconds(Track.Duration.TotalSeconds));

            _logger?.LogTrace(
                "Seek settled for {Path}: landed at {LandedMs}ms ({LandedBytes} bytes), was reporting {ReportedBytes}",
                Track.Path, timeMs, landedBytes, BytesProduced);

            Interlocked.Exchange(ref _bytesProduced, landedBytes);
            SeekSettled?.Invoke(landedBytes);
        }

        // Frame-aligned, because a byte offset that lands mid-frame is not
        // a position any part of the pipeline can act on.
        private static long BytesForSeconds(double seconds) =>
            (long)(seconds * GaplessFormat.SampleRate) * GaplessFormat.BytesPerFrame;

        // Retire() is the single end-of-life path and now releases the native
        // handles itself, so this is just the IDisposable spelling of it.
        // Nothing in the pipeline calls this - GaplessCoordinator retires
        // decoders - and that was exactly the bug: disposal lived only here,
        // unreachable, while Retire() left Media/MediaPlayer allocated.
        public void Dispose() => Retire();
    }
}
