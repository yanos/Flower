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

            if (_logger != null)
            {
                _watchdog = new System.Timers.Timer(1000);
                _watchdog.Elapsed += (_, _) => _logger.LogDebug(
                    "Decode watchdog for {Path}: State={State} IsPlaying={IsPlaying} Time={Time}ms BytesProduced={BytesProduced}",
                    Track.Path, _mediaPlayer.State, _mediaPlayer.IsPlaying, _mediaPlayer.Time, BytesProduced);
                _watchdog.Start();
            }
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
        public void Retire()
        {
            if (Interlocked.Exchange(ref _retired, 1) == 1)
                return;

            _logger?.LogInformation("Retire() for {Path}", Track.Path);
            _watchdog?.Stop();
            _mediaPlayer.Stop();
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
