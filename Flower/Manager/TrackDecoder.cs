using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using LibVLCSharp.Shared;

using Flower.Models;

namespace Flower.Manager
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

        // Diagnostic-only, temporary - null everywhere the internal
        // (LibVLC, Track, GaplessRingBuffer) constructor isn't reached with
        // a logger (there is no direct TrackDecoder unit test today).
        private readonly ILogger<TrackDecoder>? _logger;

        // Diagnostic-only, temporary: watches this decode MediaPlayer's own
        // reported State/IsPlaying/BytesProduced once a second, so a
        // seek-induced wedge shows up here too - distinguishing "the decode
        // player itself thinks it's paused/stopped" from "it thinks it's
        // still playing but has simply stopped calling OnPlay". Only logs
        // when something looks wrong (stalled/mismatched/unexpected state);
        // a healthy decode ticks silently.
        private readonly System.Timers.Timer? _watchdog;
        private long _watchdogLastBytesProduced = -1;

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
                _logger?.LogWarning("Decode MediaPlayer for {Path} encountered an error", Track.Path);
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
                _logger?.LogInformation("EndReached (high-level) for {Path}", Track.Path);
                _ = FallbackDrainIfOnDrainNeverFiresAsync();
            };

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

            _logger?.LogWarning("OnDrain never fired for {Path} within 500ms of EndReached - forcing the handover from here instead", Track.Path);
            Drained?.Invoke();
        }

        // Parses the track up front so a bad/missing/unsupported file is
        // caught before it's ever promoted to "current" or spliced into the
        // shared ring buffer, letting the coordinator degrade to "nothing
        // armed" instead of glitching playback. Safe to skip for the
        // "currently playing" role, where the user just explicitly chose it.
        public async Task<bool> PrepareAsync(CancellationToken cancellationToken = default)
        {
            // See Retire(): the gate keeps this method's native Media alive for
            // as long as Parse is using it, even if the coordinator retires this
            // decoder mid-parse.
            await _nativeGate.WaitAsync(cancellationToken);
            try
            {
                if (Volatile.Read(ref _retired) == 1)
                    return false;

                var media = EnsureMedia();
                var status = await media.Parse(MediaParseOptions.ParseLocal, 5000, cancellationToken);
                return status == MediaParsedStatus.Done;
            }
            finally
            {
                _nativeGate.Release();
            }
        }

        public void StartDecoding()
        {
            _logger?.LogInformation("StartDecoding() for {Path}", Track.Path);

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

            var stalled = state == VLCState.Playing && isPlaying && bytesProduced == _watchdogLastBytesProduced;
            var stateMismatch = state == VLCState.Playing && !isPlaying;
            var unexpectedState = Volatile.Read(ref _retired) == 0
                && state is VLCState.Error or VLCState.Stopped;

            if (stalled || stateMismatch || unexpectedState)
            {
                _logger?.LogWarning(
                    "Decode watchdog for {Path}: State={State} IsPlaying={IsPlaying} Time={Time}ms BytesProduced={BytesProduced} (Stalled={Stalled} StateMismatch={StateMismatch} UnexpectedState={UnexpectedState})",
                    Track.Path, state, isPlaying, _mediaPlayer.Time, bytesProduced, stalled, stateMismatch, unexpectedState);
            }

            _watchdogLastBytesProduced = bytesProduced;
        }

        // Seeks this decoder's own demux/decode to the given position
        // (0..1) and resets the byte-produced counter to match, so
        // Time/Position stay correct across the seek. LibVLC's own flush
        // callback (OnFlush) fires as a side effect, discarding whatever
        // pre-seek audio was already sitting in this decoder's current
        // target ring.
        public void Seek(float position)
        {
            _logger?.LogInformation(
                "Seek({Position}) on {Path}: State={State} IsPlaying={IsPlaying} before seek",
                position, Track.Path, _mediaPlayer.State, _mediaPlayer.IsPlaying);
            _mediaPlayer.Position = position;
            var targetSeconds = Track.Duration.TotalSeconds * position;
            var targetBytes = (long)(targetSeconds * GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);
            Interlocked.Exchange(ref _bytesProduced, targetBytes);

            // Defensive: if LibVLC's seek sequence internally pauses the
            // decode pipeline as part of repositioning and never resumes it
            // (suspected, pending confirmation from the OnPause/OnResume
            // logging above and the watchdog), this nudges it back to
            // playing. Harmless no-op if it's already playing.
            _mediaPlayer.SetPause(false);
        }

        // Drains everything currently buffered in this decoder's target
        // ring into newTarget, then switches future output to newTarget -
        // see RetargetableRingWriter.
        public void PromoteTarget(GaplessRingBuffer newTarget) => _writer.PromoteTarget(newTarget);

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

            _logger?.LogInformation("Retire() for {Path}", Track.Path);
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
            return _media ??= path.Contains("://")
                ? new Media(_libVLC, path, FromType.FromLocation)
                : new Media(_libVLC, path);
        }

        private void OnPlay(IntPtr data, IntPtr samples, uint count, long pts)
        {
            if (Volatile.Read(ref _retired) == 1)
                return;

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

            Interlocked.Add(ref _bytesProduced, byteCount);
        }

        private void OnPause(IntPtr data, long pts)
        {
            _logger?.LogInformation("OnPause for {Path}", Track.Path);
        }

        private void OnResume(IntPtr data, long pts)
        {
            _logger?.LogInformation("OnResume for {Path}", Track.Path);
        }

        private void OnFlush(IntPtr data, long pts)
        {
            if (Volatile.Read(ref _retired) == 1)
                return;

            _logger?.LogInformation("OnFlush for {Path} - resetting target ring", Track.Path);

            _writer.ResetTarget();
        }

        private void OnDrain(IntPtr data)
        {
            _drainFired = true;

            if (Volatile.Read(ref _retired) == 1)
                return;

            _logger?.LogInformation("OnDrain for {Path}", Track.Path);
            Drained?.Invoke();
        }

        // Retire() is the single end-of-life path and now releases the native
        // handles itself, so this is just the IDisposable spelling of it.
        // Nothing in the pipeline calls this - GaplessCoordinator retires
        // decoders - and that was exactly the bug: disposal lived only here,
        // unreachable, while Retire() left Media/MediaPlayer allocated.
        public void Dispose() => Retire();
    }
}
