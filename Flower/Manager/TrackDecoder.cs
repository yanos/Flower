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
    // time. The _targetGate lock makes "drain whatever's in the old target,
    // then start writing to the new one" atomic with respect to this
    // decoder's own producer thread, so no bytes can be produced into a
    // target that's mid-retarget and get lost or duplicated.
    public sealed class TrackDecoder : ITrackDecoder
    {
        private readonly LibVLC _libVLC;
        private readonly MediaPlayer _mediaPlayer;
        private readonly object _targetGate = new();
        private Media? _media;
        private byte[] _scratch = [];
        private GaplessRingBuffer _target;
        private long _bytesProduced;
        private int _retired;
        private volatile bool _drainFired;

        // Diagnostic-only, temporary - null everywhere the internal
        // (LibVLC, Track, GaplessRingBuffer) constructor isn't reached with
        // a logger (there is no direct TrackDecoder unit test today).
        private readonly ILogger<TrackDecoder>? _logger;

        // Diagnostic-only, temporary: logs this decode MediaPlayer's own
        // reported State/IsPlaying/Time once a second, so a seek-induced
        // wedge shows up here too - distinguishing "the decode player
        // itself thinks it's paused/stopped" from "it thinks it's still
        // playing but has simply stopped calling OnPlay".
        private readonly System.Timers.Timer? _watchdog;

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
            _target = initialTarget;
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
                _watchdog.Elapsed += (_, _) => _logger.LogDebug(
                    "Decode watchdog for {Path}: State={State} IsPlaying={IsPlaying} Time={Time}ms BytesProduced={BytesProduced}",
                    Track.Path, _mediaPlayer.State, _mediaPlayer.IsPlaying, _mediaPlayer.Time, BytesProduced);
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
            var media = EnsureMedia();
            var status = await media.Parse(MediaParseOptions.ParseLocal, 5000, cancellationToken);
            return status == MediaParsedStatus.Done;
        }

        public void StartDecoding()
        {
            var media = EnsureMedia();
            _mediaPlayer.SetAudioFormat(GaplessFormat.LibVlcFourCc, GaplessFormat.SampleRate, GaplessFormat.Channels);
            _mediaPlayer.SetAudioCallbacks(OnPlay, OnPause, OnResume, OnFlush, OnDrain);

            if (!_mediaPlayer.Play(media))
                Faulted?.Invoke();
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
        // done under _targetGate so the switch is atomic relative to this
        // decoder's own OnPlay callback, which also takes _targetGate.
        public void PromoteTarget(GaplessRingBuffer newTarget)
        {
            lock (_targetGate)
            {
                Span<byte> chunk = stackalloc byte[4096];
                int read;
                while ((read = _target.Read(chunk)) > 0)
                    newTarget.Write(chunk[..read]);

                _target = newTarget;
            }
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

            _logger?.LogInformation("Retire() for {Path}", Track.Path);
            _watchdog?.Stop();

            var mediaPlayer = _mediaPlayer;
            var path = Track.Path;
            var logger = _logger;
            Task.Run(() =>
            {
                try
                {
                    mediaPlayer.Stop();
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "MediaPlayer.Stop() failed during Retire() for {Path}", path);
                }
            });
        }

        private Media EnsureMedia()
        {
            // Android's MediaStore importer hands back content:// URIs
            // rather than filesystem paths; those need FromLocation, not
            // the default FromPath.
            return _media ??= Track.Path is { } path && path.Contains("://")
                ? new Media(_libVLC, path, FromType.FromLocation)
                : new Media(_libVLC, Track.Path);
        }

        private void OnPlay(IntPtr data, IntPtr samples, uint count, long pts)
        {
            if (Volatile.Read(ref _retired) == 1)
                return;

            var byteCount = checked((int)count * GaplessFormat.BytesPerFrame);
            if (_scratch.Length < byteCount)
                _scratch = new byte[byteCount];

            Marshal.Copy(samples, _scratch, 0, byteCount);

            lock (_targetGate)
            {
                _target.Write(_scratch.AsSpan(0, byteCount));
            }

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

            lock (_targetGate)
            {
                _target.Reset();
            }
        }

        private void OnDrain(IntPtr data)
        {
            _drainFired = true;

            if (Volatile.Read(ref _retired) == 1)
                return;

            _logger?.LogInformation("OnDrain for {Path}", Track.Path);
            Drained?.Invoke();
        }

        public void Dispose()
        {
            Retire();
            _watchdog?.Dispose();
            _media?.Dispose();
            _mediaPlayer.Dispose();
        }
    }
}
