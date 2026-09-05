using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Models;
using Flower.Services;

namespace Flower.Audio.Ffmpeg
{
    // ITrackDecoder over the flower-ffmpeg façade - the second implementation
    // GaplessCoordinator can drive, alongside the LibVLC-based TrackDecoder it
    // was written for. Everything above ITrackDecoder is unchanged: the
    // decode-ahead role, the staging ring, the retarget at handover.
    //
    // The shape below is different from TrackDecoder's in one way that is
    // worth stating, because it is the reason several of that class's hardest
    // bugs cannot occur here. LibVLC pushes: it owns a thread, calls OnPlay
    // when it feels like it, and a seek is a request whose effects arrive
    // later on callbacks that have to be correlated back to it (hence
    // _seekRequested, _seekAwaitingFirstSample, and OnFlush having to tell a
    // seek's flush from a route change's). FFmpeg pulls: this class owns the
    // thread and asks for samples, so a seek is a function call that returns
    // where it landed. The seek is still posted to the decode thread rather
    // than performed on the caller's, because one FFmpeg decoder belongs to
    // one thread - but the correlation problem is gone, not merely handled.
    //
    // Delivers GaplessFormat's canonical PCM. That format is 24 bits because
    // this decoder can fill it - the LibVLC decoder it replaced could not,
    // whatever it was asked for, which is why it is gone - and the request
    // below is what asks the façade for them. Choosing the *source's* own rate
    // and channel layout as well - no resample at all - is direct mode's job
    // and needs the output device to have agreed to it first; see
    // docs/AUDIOPHILE-PLAN.md.
    public sealed class FfmpegTrackDecoder : ITrackDecoder
    {
        // One decode read, and so also the granularity at which a retire or a
        // seek interrupts one. A quarter of a second of canonical PCM: small
        // enough that neither is felt, large enough that the P/Invoke and the
        // ring's own locking are not the work.
        private const int ReadBufferBytes = 48 * 1024;

        private readonly RetargetableRingWriter _writer;
        private readonly Func<bool> _isRetired;
        private readonly ILogger<FfmpegTrackDecoder>? _logger;

        // Captured once, at construction, rather than read live from
        // GaplessFormat on each use. In the app the two are the same thing -
        // the canonical format is negotiated before any decoder exists and
        // frozen after - but a decoder that re-reads it is a decoder whose
        // frame size can change halfway through a track, which is not a state
        // anything downstream is written for. Taking it as a parameter also
        // lets Flower.DeviceChecks run one decoder at S16 and another at S24
        // in the same process without moving a global out from under either.
        private readonly PcmSampleFormat _sampleFormat;
        private readonly int _bytesPerFrame;

        private FfmpegDecoder? _decoder;
        private SeekableHttpStream? _remoteStream;
        private Thread? _thread;

        private long _bytesProduced;
        private int _retired;
        private int _started;

        // The decode loop has ended of its own accord - drained, faulted, or
        // returned. Deliberately distinct from _retired, which is somebody
        // else's decision about this decoder rather than its own report about
        // itself: the watchdog below is interested in exactly the gap between
        // the two.
        private int _finished;
        private int _reported;

        private System.Timers.Timer? _watchdog;
        private long _watchdogLastBytesProduced = -1;
        private long _watchdogLastBackpressureWaits = -1;

        // -1 when no seek is outstanding. Written by whoever calls Seek, read
        // and cleared by the decode thread.
        private long _pendingSeekMs = -1;

        public Track Track { get; }

        public long BytesProduced => Interlocked.Read(ref _bytesProduced);

        public event Action? Drained;
        public event Action? Faulted;
        public event Action<long>? SeekSettled;

        public FfmpegTrackDecoder(Track track, GaplessRingBuffer initialTarget,
                                  ILogger<FfmpegTrackDecoder>? logger = null,
                                  PcmSampleFormat? sampleFormat = null)
        {
            Track = track;
            _sampleFormat = sampleFormat ?? GaplessFormat.SampleFormat;
            _bytesPerFrame = GaplessFormat.BytesPerSampleOf(_sampleFormat) * (int)GaplessFormat.Channels;
            _writer = new RetargetableRingWriter(initialTarget);
            _isRetired = () => Volatile.Read(ref _retired) == 1;
            _logger = logger;
        }

        // Opening is the whole of the prepare: unlike LibVLC's parse, which
        // answers Skipped for a stream it was handed callbacks for, FFmpeg
        // reads the container here and either has an audio stream or does not.
        // So Ready genuinely means decodable, not merely reachable.
        public async Task<DecodePrepareResult> PrepareAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _retired) == 1)
                return DecodePrepareResult.Retired;

            if (Track.Path is not { } path)
                throw new InvalidOperationException($"Cannot decode \"{Track.Title}\" - it has no local Path (undownloaded sync placeholder).");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OpenTimeoutMs);

            try
            {
                await Task.Run(() => Open(path, timeout.Token), timeout.Token);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _retired) == 1)
            {
                return DecodePrepareResult.Retired;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning("Opening {Path} did not answer within {TimeoutMs}ms", LogPath.Short(path), OpenTimeoutMs);
                return DecodePrepareResult.TimedOut;
            }
            catch (OperationCanceledException)
            {
                return DecodePrepareResult.Retired;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not open {Path} for decoding", LogPath.Short(path));
                return DecodePrepareResult.Failed;
            }

            if (Volatile.Read(ref _retired) == 1)
            {
                Retire();
                return DecodePrepareResult.Retired;
            }

            LogOpened(path);

            return DecodePrepareResult.Ready;
        }

        private void LogOpened(string path)
        {
            var format = _decoder!.Format;
            _logger?.LogInformation(
                "Opened {Path}: Source={SourceRate}Hz/{SourceDepth}-bit/{SourceChannels}ch Delivering={Rate}Hz/{Format}/{Channels}ch Duration={DurationMs}ms",
                LogPath.Short(path), format.SourceSampleRate, format.SourceBitDepth, format.SourceChannels,
                format.SampleRate, format.SampleFormat, format.Channels,
                format.Duration?.TotalMilliseconds ?? -1);
        }

        // Same budget as TrackDecoder's parse, and for the same reason: it is
        // how long a stopped or unreachable server takes to be reported, and
        // it runs off the lock while the current track keeps playing.
        private const int OpenTimeoutMs = 5000;

        private void Open(string path, CancellationToken cancellationToken)
        {
            if (IsRemote(path))
            {
                // The same stream LibVLC reads through today, handed to
                // FFmpeg's AVIOContext callbacks instead - which is the point
                // of having built it before this: range requests, a known
                // length, a real seek, and every byte fetched on Flower's own
                // pinned client.
                _remoteStream = new SeekableHttpStream(AudioHttpClient, new Uri(path), logger: _logger);
                _remoteStream.ProbeAsync(cancellationToken).GetAwaiter().GetResult();
                _decoder = FfmpegDecoder.OpenStream(
                    _remoteStream,
                    FormatFor(_sampleFormat),
                    (int)GaplessFormat.SampleRate,
                    (int)GaplessFormat.Channels,
                    DemuxerHintFor(Track),
                    logger: _logger);
            }
            else
            {
                _decoder = FfmpegDecoder.OpenPath(
                    path,
                    FormatFor(_sampleFormat),
                    (int)GaplessFormat.SampleRate,
                    (int)GaplessFormat.Channels);
            }
        }

        // A prepare is an optimisation, not a precondition. GaplessCoordinator
        // calls PrepareAsync only on the decode-ahead path; pressing play on a
        // track goes straight from Play() to here, and LibVLC's TrackDecoder
        // supports that by doing its own open inside StartDecoding. A decoder
        // that instead required a prepare faulted on every single press of
        // play, and only tracks that happened to be armed ahead of time ever
        // opened at all - the golden path was the one path that never worked.
        //
        // So an unprepared start opens on the decode thread rather than here:
        // this runs under GaplessCoordinator's lock, and opening a remote
        // track is a network round trip.
        // What the façade is asked to hand back for a given pipeline format.
        // This used to live next to the decoder election, so the mapping
        // between the pipeline's format and FFmpeg's own sat beside the choice
        // that produced it; with one decoder left it belongs to the decoder.
        public static FfmpegSampleFormat FormatFor(PcmSampleFormat format) =>
            format == PcmSampleFormat.S24 ? FfmpegSampleFormat.S24 : FfmpegSampleFormat.S16;

        public void StartDecoding()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
                return;
            if (Volatile.Read(ref _retired) == 1)
                return;

            _thread = new Thread(DecodeLoop)
            {
                IsBackground = true,
                Name = $"flower-decode {LogPath.Short(Track.Path)}",
            };
            _thread.Start();

            // Only where somebody is listening: a decoder built without a
            // logger - the device checks, most of the tests - has nowhere to
            // report to, and a once-a-second timer per decoder that can only
            // write into the void is pure cost.
            if (_logger is not null)
            {
                _watchdog = new System.Timers.Timer(1000) { AutoReset = true };
                _watchdog.Elapsed += (_, _) => CheckWatchdog();
                _watchdog.Start();
            }
        }

        // Only logs when something looks wrong, so a healthy decode does not
        // spam a line a second for the whole track.
        //
        // The LibVLC decoder had one of these and it went out with the
        // decoder, which left the app with no way at all to tell a wedged
        // decode from a long one. This is the same idea against a different
        // set of facts: there is no player to ask for a state here, so the
        // only authorities are the byte counter, the writer's own
        // parked-for-room count, and whether the decode thread is still
        // running.
        private void CheckWatchdog()
        {
            var retired  = Volatile.Read(ref _retired) == 1;
            var finished = Volatile.Read(ref _finished) == 1;
            var decoding = !retired && !finished;

            var bytesProduced     = BytesProduced;
            var backpressureWaits = _writer.BackpressureWaits;
            var target            = _writer.Target;

            var stalled = IsStalled(
                decoding,
                bytesProduced, _watchdogLastBytesProduced,
                backpressureWaits, _watchdogLastBackpressureWaits);

            // The other half, and the one that has no LibVLC equivalent
            // because LibVLC always had a state to read: the loop ended by
            // itself, nobody retired this decoder, and neither Drained nor
            // Faulted was raised. The coordinator is then waiting for an event
            // that will never come, and the symptom is a track that simply
            // stops with nothing advancing after it.
            var endedSilently = finished && !retired && Volatile.Read(ref _reported) == 0;

            if (stalled || endedSilently)
            {
                _logger?.LogWarning(
                    "Decode watchdog for {Path}: BytesProduced={BytesProduced} BackpressureWaits={BackpressureWaits} TargetRead={TargetRead} TargetWritten={TargetWritten} TargetAvailable={TargetAvailable}/{TargetCapacity} TargetGeneration={TargetGeneration} (Stalled={Stalled} EndedSilently={EndedSilently})",
                    LogPath.Short(Track.Path), bytesProduced, backpressureWaits,
                    target.TotalBytesRead, target.TotalBytesWritten, target.AvailableBytes,
                    target.Capacity, target.Generation, stalled, endedSilently);
            }

            _watchdogLastBytesProduced     = bytesProduced;
            _watchdogLastBackpressureWaits = backpressureWaits;

            // A finished decoder has nothing left to say. Stopping here rather
            // than only in Retire matters because a decoder that drained is
            // often not retired for a while - the coordinator keeps it around
            // while its tail plays out - and endedSilently would otherwise
            // report the same thing every second until it was.
            if (!decoding)
                StopWatchdog();
        }

        private void StopWatchdog()
        {
            var watchdog = Interlocked.Exchange(ref _watchdog, null);
            if (watchdog is null)
                return;

            watchdog.Stop();
            watchdog.Dispose();
        }

        // Pulled out of CheckWatchdog so it can be tested: the surrounding
        // method needs a live decode thread and a native decoder, and the
        // interesting cases are exactly the ones a real decode cannot be asked
        // to produce on demand. Getting this predicate wrong is also not a
        // quiet failure - the LibVLC version without the backpressure term
        // logged 2430 false alarms in a single day on one phone, every one of
        // them with the target ring full and nothing whatsoever wrong.
        //
        // The two "last" arguments are the previous tick's samples, -1 on the
        // first tick, where nothing can be concluded from a delta yet.
        internal static bool IsStalled(
            bool decoding,
            long bytesProduced,
            long lastBytesProduced,
            long backpressureWaits,
            long lastBackpressureWaits)
        {
            if (!decoding)
                return false;

            // First tick: no previous sample of either counter, so there is no
            // delta to conclude anything from. The watchdog runs once a second
            // and there is always a next one.
            if (lastBytesProduced < 0)
                return false;

            if (bytesProduced != lastBytesProduced)
                return false;

            // A decoder that filled its ring and is waiting for playback to
            // drain it produces no bytes either, and for an armed decoder that
            // is most of a track rather than a wedge.
            //
            // The signal is the writer's own parked-for-room count rather than
            // the ring's free space, because free space is sampled at an
            // arbitrary instant - the reader drains a period every few
            // milliseconds, so a full ring reads as briefly not-full - whereas
            // "did it park at any point during this window" is exactly the
            // question and cannot be lost to sampling.
            var waitedForRoom = lastBackpressureWaits >= 0 && backpressureWaits != lastBackpressureWaits;
            return !waitedForRoom;
        }

        // The open PrepareAsync would have done, on the decode thread, for a
        // decoder that was started without one. Reports a failure the same way
        // a failed decode does - Faulted - because that is the only channel a
        // started decoder has left; PrepareAsync's richer DecodePrepareResult
        // has no caller here.
        private bool OpenForUnpreparedStart()
        {
            if (Track.Path is not { } path)
            {
                _logger?.LogWarning("StartDecoding() for \"{Title}\" which has no local path", Track.Title);
                ReportFaulted();
                return false;
            }

            using var timeout = new CancellationTokenSource(OpenTimeoutMs);
            try
            {
                Open(path, timeout.Token);
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _retired) == 1)
                    return false;

                _logger?.LogWarning(ex, "Could not open {Path} for decoding", LogPath.Short(path));
                ReportFaulted();
                return false;
            }

            if (Volatile.Read(ref _retired) == 1)
            {
                Retire();
                return false;
            }

            LogOpened(path);
            return true;
        }

        // Every exit this decoder has that the coordinator is waiting to hear
        // about goes through one of these two, so that "the loop ended and
        // nobody was told" is a state the watchdog can name rather than a
        // silence indistinguishable from a slow track.
        private void ReportDrained()
        {
            Interlocked.Exchange(ref _reported, 1);
            Drained?.Invoke();
        }

        private void ReportFaulted()
        {
            Interlocked.Exchange(ref _reported, 1);
            Faulted?.Invoke();
        }

        private void DecodeLoop()
        {
            try
            {
                DecodeUntilDone();
            }
            finally
            {
                Interlocked.Exchange(ref _finished, 1);
            }
        }

        private void DecodeUntilDone()
        {
            var buffer = new byte[ReadBufferBytes];

            if (_decoder is null && !OpenForUnpreparedStart())
                return;

            var decoder = _decoder!;

            try
            {
                while (Volatile.Read(ref _retired) == 0)
                {
                    var requested = Interlocked.Exchange(ref _pendingSeekMs, -1);
                    if (requested >= 0)
                        ApplySeek(decoder, requested);

                    int read;
                    try
                    {
                        read = decoder.Read(buffer);
                    }
                    catch (Exception ex)
                    {
                        if (Volatile.Read(ref _retired) == 1)
                            return;
                        _logger?.LogWarning(ex, "Decoding {Path} failed", LogPath.Short(Track.Path));
                        ReportFaulted();
                        return;
                    }

                    if (read == 0)
                    {
                        if (Volatile.Read(ref _retired) == 0)
                        {
                            _logger?.LogInformation(
                                "Decode drain for {Path}: TaggedDurationMs={TaggedDurationMs} DecodedMs={DecodedMs} BytesProduced={BytesProduced}",
                                LogPath.Short(Track.Path), Track.Duration.TotalMilliseconds,
                                BytesProduced / (double)(GaplessFormat.SampleRate * _bytesPerFrame) * 1000,
                                BytesProduced);
                            ReportDrained();
                        }
                        return;
                    }

                    // Parks here for as long as the target ring stays full,
                    // which for an armed decoder that filled its staging ring
                    // is most of a track. The predicate is how a retire gets
                    // out of that; a seek gets out through ResetTarget's
                    // generation bump - see RetargetableRingWriter.Write.
                    _writer.Write(buffer.AsSpan(0, read), _isRetired);

                    // Counted only once the whole buffer is actually in the
                    // ring. Write returns early on a retire or a seek having
                    // written part of it, and counting the rest would push the
                    // reported position past audio nobody will hear - a retire
                    // measured exactly one buffer of phantom progress before
                    // this check existed.
                    if (Volatile.Read(ref _retired) == 0 && Interlocked.Read(ref _pendingSeekMs) < 0)
                        Interlocked.Add(ref _bytesProduced, read);
                }
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _retired) == 1)
                    return;
                _logger?.LogError(ex, "The decode thread for {Path} ended unexpectedly", LogPath.Short(Track.Path));
                ReportFaulted();
            }
        }

        private void ApplySeek(FfmpegDecoder decoder, long requestedMs)
        {
            try
            {
                var landed = decoder.Seek(TimeSpan.FromMilliseconds(requestedMs));

                // Second reset, after the seek rather than before it. Seek()
                // resets to unblock this thread out of a full ring; anything
                // it managed to write between that reset and this line is
                // still pre-seek audio, and this is what drops it.
                _writer.ResetTarget();

                var landedBytes = Math.Clamp(BytesForSeconds(landed.TotalSeconds), 0, DurationBytes());
                Interlocked.Exchange(ref _bytesProduced, landedBytes);

                _logger?.LogTrace("Seek settled for {Path}: asked {RequestedMs}ms, landed {LandedMs}ms ({LandedBytes} bytes)",
                                  LogPath.Short(Track.Path), requestedMs, landed.TotalMilliseconds, landedBytes);
                SeekSettled?.Invoke(landedBytes);
            }
            catch (Exception ex)
            {
                // A refused seek is not a dead track - a forward-only stream
                // simply cannot go there. Decode continues from where it was,
                // and the position the caller was told stands.
                _logger?.LogWarning(ex, "Seeking {Path} to {RequestedMs}ms failed", LogPath.Short(Track.Path), requestedMs);
            }
        }

        // Posted to the decode thread rather than performed here: one FFmpeg
        // decoder belongs to one thread. The provisional position is published
        // immediately so the scrubber moves on the press, and corrected to
        // where the demuxer actually landed when the seek runs - see
        // SeekSettled.
        public void Seek(float position)
        {
            var requestedMs = (long)(Track.Duration.TotalMilliseconds * Math.Clamp(position, 0f, 1f));
            Interlocked.Exchange(ref _pendingSeekMs, requestedMs);
            Interlocked.Exchange(ref _bytesProduced, Math.Clamp(BytesForSeconds(requestedMs / 1000.0), 0, DurationBytes()));

            // Discards the pre-seek audio already buffered, and in doing so
            // frees the room that lets a decode thread parked on a full ring
            // notice the request.
            _writer.ResetTarget();
        }

        public PromotionSplice PrimeTarget(GaplessRingBuffer newTarget) => _writer.PrimeTarget(newTarget);

        public PromotionSplice PromoteTarget(GaplessRingBuffer newTarget)
        {
            var oldTarget = _writer.Target;
            var stagedBytes = oldTarget.AvailableBytes;

            var splice = _writer.PromoteTarget(newTarget);

            _logger?.LogInformation(
                "Decoder output promotion completed for {Path}: MovedBytes={MovedBytes} SnapshotStagedBytes={SnapshotStagedBytes} MsToFirstByte={MsToFirstByte} TotalMs={TotalMs}",
                LogPath.Short(Track.Path), splice.BytesMoved, stagedBytes,
                splice.MillisecondsToFirstByte, splice.TotalMilliseconds);

            return splice;
        }

        public void Retire()
        {
            if (Interlocked.Exchange(ref _retired, 1) == 1)
                return;

            _logger?.LogTrace("Retire() for {Path}", LogPath.Short(Track.Path));

            StopWatchdog();

            // Wakes a decode thread parked on a full ring so it notices the
            // retirement now rather than up to 20ms from now.
            //
            // This used to call ResetTarget, which also emptied the ring - and
            // when a track ends naturally that ring is the shared one, holding
            // every sample decoded ahead of the render callback. So the tail
            // of every track was discarded at the moment its decode finished:
            // up to a full shared ring of audio, which is where the "decoder
            // completed while N ms of PCM remains buffered" warning in
            // GaplessCoordinator was pointing all along. A one-second fixture
            // decodes in about 13ms, so it lost the entire track and rendered
            // a single 4096-byte buffer.
            //
            // Nothing needed the reset even to unblock the thread: Write parks
            // on Monitor.Wait(_gate, 20), so it re-checks the predicate every
            // 20ms regardless.
            _writer.Wake();

            var thread = _thread;
            var decoder = _decoder;
            var stream = _remoteStream;
            _decoder = null;
            _remoteStream = null;

            // Off the caller's thread: this is called from the coordinator
            // during a skip, and joining a decode thread that is mid-read of a
            // slow network stream would stall the UI.
            _ = Task.Run(() =>
            {
                // Joined rather than raced: the native decoder must not be
                // closed while a read is inside it, and the read holds a
                // pointer this call frees.
                if (thread is not null && !thread.Join(TimeSpan.FromSeconds(5)))
                    _logger?.LogWarning("The decode thread for {Path} did not stop within 5s; leaking its decoder rather than closing it underneath",
                                        LogPath.Short(Track.Path));
                else
                    decoder?.Dispose();

                stream?.Dispose();
            });
        }

        public void Dispose() => Retire();

        private long DurationBytes() =>
            Track.Duration > TimeSpan.Zero
                ? BytesForSeconds(Track.Duration.TotalSeconds)
                : long.MaxValue;

        private long BytesForSeconds(double seconds) =>
            (long)(seconds * GaplessFormat.SampleRate) * _bytesPerFrame;

        private static bool IsRemote(string path) =>
            path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // FFmpeg's own demuxer names, not LibVLC's module names, and named
        // for the same containers TrackDecoder.DemuxHintFor names and no
        // others.
        //
        // This used to list mp3, flac and wav as well, on the reasoning that
        // skipping the probe saves a round trip on a remote track. It cost an
        // album instead. The extension is the *catalog's* (Child.Suffix, kept
        // as OriginFileExtension), and the catalog describes the file on the
        // server's disk - not necessarily the bytes on the wire, which a
        // server is free to transcode, and not necessarily right in the first
        // place. Forcing a demuxer discards FFmpeg's probe entirely, so a
        // suffix that disagrees with the bytes is not a slow open, it is
        // "Failed to find two consecutive MPEG audio frames" and a track that
        // will not play at all. TrackDecoder's own hint already carried this
        // conclusion in its comment - "forcing the wrong demuxer is worse than
        // probing" - and this is that lesson, learned twice.
        //
        // The MP4 family stays because there the hint buys something a probe
        // cannot: a moov atom at the end of a stream that cannot be seeked.
        // Even there the force is a preference rather than a verdict - see
        // FfmpegDecoder.OpenStream, which falls back to probing when a forced
        // demuxer will not open the stream.
        internal static string? DemuxerHintFor(Track track)
        {
            var extension = track.OriginFileExtension?.TrimStart('.');
            if (string.IsNullOrEmpty(extension))
                extension = track.Path is { } p && !p.Contains("://") ? System.IO.Path.GetExtension(p).TrimStart('.') : null;

            return extension?.ToLowerInvariant() switch
            {
                "m4a" or "m4b" or "mp4" or "alac" => "mp4",
                _ => null,
            };
        }

        // Shared with TrackDecoder's own, and for the same reasons - see
        // TrackDecoder.CreateAudioHttpClient. Both decoders exist at once
        // during the migration, and a second client would double the socket
        // pool for nothing.
        private static readonly HttpClient AudioHttpClient = CreateAudioHttpClient();

        private static HttpClient CreateAudioHttpClient()
        {
            var client = PeerHttpClient.CreateSigned();
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }
    }
}
