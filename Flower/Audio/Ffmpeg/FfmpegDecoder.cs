using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Microsoft.Extensions.Logging;

namespace Flower.Audio.Ffmpeg
{
    // What the caller wants the decoder to hand back. S24 is packed three-byte
    // little-endian, which is what miniaudio's ma_format_s24 takes and what a
    // 24-bit source can be delivered in without losing a bit - the thing
    // LibVLC's amem seam cannot do at all.
    public enum FfmpegSampleFormat
    {
        S16 = 0,
        S24 = 1,
        S32 = 2,
        F32 = 3,
    }

    public readonly record struct FfmpegAudioFormat(
        int SampleRate,
        int Channels,
        FfmpegSampleFormat SampleFormat,
        int SourceBitDepth,
        int SourceSampleRate,
        int SourceChannels,
        TimeSpan? Duration)
    {
        public int BytesPerFrame => SampleFormat switch
        {
            FfmpegSampleFormat.S16 => 2 * Channels,
            FfmpegSampleFormat.S24 => 3 * Channels,
            _ => 4 * Channels,
        };
    }

    public sealed class FfmpegDecodeException(string message, int code)
        : IOException($"{message}: {FfmpegNative.Describe(code)}")
    {
        public int Code { get; } = code;
    }

    // One decode of one source, over either a file path or a managed Stream.
    // Not thread-safe: like the native decoder it wraps, one instance belongs
    // to one decode thread.
    //
    // The Stream overload is what makes this the answer to more than bit
    // depth. FFmpeg takes read and seek callbacks, so a track streamed from a
    // server is seekable by construction rather than by whatever the
    // platform's HTTP layer decided - and the stream handed in is
    // SeekableHttpStream, unchanged from the one LibVLC reads through today.
    public sealed unsafe class FfmpegDecoder : IDisposable
    {
        private IntPtr _handle;
        private readonly Stream? _stream;
        private readonly bool _ownsStream;
        // Pins this instance for as long as the native decoder can call back
        // into it. The callbacks are static so they survive AOT compilation on
        // iOS; this handle is how they find their way back to an instance.
        private GCHandle _self;

        public FfmpegAudioFormat Format { get; }

        private FfmpegDecoder(IntPtr handle, Stream? stream, bool ownsStream, GCHandle self)
        {
            _handle = handle;
            _stream = stream;
            _ownsStream = ownsStream;
            _self = self;

            var rc = FfmpegNative.GetFormat(handle, out var format);
            if (rc != FfmpegNative.Ok)
            {
                Dispose();
                throw new FfmpegDecodeException("Could not read the decoded audio format", rc);
            }

            Format = new FfmpegAudioFormat(
                format.SampleRate,
                format.Channels,
                (FfmpegSampleFormat)format.SampleFormat,
                format.SourceBitDepth,
                format.SourceSampleRate,
                format.SourceChannels,
                format.DurationMs < 0 ? null : TimeSpan.FromMilliseconds(format.DurationMs));
        }

        // Whether this build can decode through the façade at all - i.e.
        // whether flower_ffmpeg is present, loadable, and the ABI this
        // assembly was compiled against.
        //
        // A question worth asking rather than assuming, because the answer is
        // "no" on four of the five platform heads today: only macOS has a
        // built artifact (see native/ffmpeg/README.md's status table). Asking
        // it costs one P/Invoke, once, and the alternative is a
        // DllNotFoundException thrown from a decode thread at the moment
        // somebody presses play.
        //
        // Cached because a failure is permanent for the process: the resolver
        // has already walked every candidate path by the time this returns.
        public static bool IsAvailable => _isAvailable ??= ProbeAvailability();

        private static bool? _isAvailable;

        private static bool ProbeAvailability()
        {
            try
            {
                return FfmpegNative.AbiVersion() == FfmpegNative.ExpectedAbiVersion;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (BadImageFormatException)
            {
                // A library built for the wrong architecture - a stale x64
                // artifact on an arm64 machine, most likely.
                return false;
            }
        }

        // Verified once rather than per open: a library whose ABI does not
        // match reads the format struct at the wrong offsets and reports
        // plausible nonsense instead of failing.
        private static void EnsureAbi()
        {
            var actual = FfmpegNative.AbiVersion();
            if (actual != FfmpegNative.ExpectedAbiVersion)
                throw new FfmpegDecodeException(
                    $"flower_ffmpeg reports ABI {actual}, this build expects {FfmpegNative.ExpectedAbiVersion}",
                    FfmpegNative.Ok);
        }

        // sampleRate/channels of 0 ask for the source's own, which is how a
        // bit-perfect open asks for no conversion at all.
        public static FfmpegDecoder OpenPath(string path, FfmpegSampleFormat format, int sampleRate = 0, int channels = 0)
        {
            EnsureAbi();
            var rc = FfmpegNative.OpenPath(path, (int)format, sampleRate, channels, out var handle);
            if (rc != FfmpegNative.Ok)
                throw new FfmpegDecodeException($"Could not open {path}", rc);

            return new FfmpegDecoder(handle, stream: null, ownsStream: false, default);
        }

        // formatHint names a demuxer to prefer (FFmpeg's short name, e.g.
        // "mp4"), skipping the probe on a stream whose container the caller
        // already knows.
        //
        // A preference rather than a verdict: forcing a demuxer discards
        // FFmpeg's probe, so a hint that is wrong about the bytes does not
        // open slowly, it does not open at all. The hint's source is a
        // catalog entry describing a file on a server's disk, and the bytes
        // on the wire are whatever that server chose to send - so being wrong
        // is an ordinary event, not a corrupt library. When the forced open
        // fails the stream is rewound and opened again by probing, which is
        // what would have happened with no hint at all.
        public static FfmpegDecoder OpenStream(Stream stream, FfmpegSampleFormat format,
                                               int sampleRate = 0, int channels = 0,
                                               string? formatHint = null, bool ownsStream = false,
                                               ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            EnsureAbi();

            if (formatHint is { Length: > 0 } && stream.CanSeek)
            {
                try
                {
                    // ownsStream deliberately false on this attempt: a failure
                    // here is not the end of the stream's life, it is the
                    // start of the second attempt on the same stream.
                    return OpenStreamAs(stream, format, sampleRate, channels, formatHint, ownsStream: false);
                }
                catch (FfmpegDecodeException rejected)
                {
                    // Logged rather than swallowed: the fallback means a
                    // mislabelled track plays, but the label is still wrong,
                    // and this is the only place that ever finds out.
                    logger?.LogWarning(
                        rejected,
                        "The {Hint} demuxer would not open this stream; probing for the real container instead",
                        formatHint);

                    // Back to where the forced attempt started reading. A
                    // demuxer that refused the stream still consumed some of
                    // it, and probing from the middle of a file finds nothing.
                    stream.Seek(0, SeekOrigin.Begin);
                }

                return OpenStreamAs(stream, format, sampleRate, channels, formatHint: null, ownsStream);
            }

            return OpenStreamAs(stream, format, sampleRate, channels, formatHint, ownsStream);
        }

        private static FfmpegDecoder OpenStreamAs(Stream stream, FfmpegSampleFormat format,
                                                  int sampleRate, int channels,
                                                  string? formatHint, bool ownsStream)
        {
            var state = new StreamState(stream);
            var self = GCHandle.Alloc(state);
            long size;
            try
            {
                size = stream.CanSeek ? stream.Length : -1;
            }
            catch (Exception)
            {
                size = -1;
            }

            var rc = FfmpegNative.OpenIo(
                GCHandle.ToIntPtr(self),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int>)&ReadTrampoline,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, long, int, long>)&SeekTrampoline,
                size,
                stream.CanSeek ? 1 : 0,
                formatHint,
                (int)format, sampleRate, channels,
                out var handle);

            if (rc != FfmpegNative.Ok)
            {
                self.Free();
                if (ownsStream)
                    stream.Dispose();
                throw new FfmpegDecodeException("Could not open the audio stream", rc);
            }

            return new FfmpegDecoder(handle, stream, ownsStream, self);
        }

        // Fills as much of buffer as the source has left. Returns 0 at the end
        // of the track - and only at the end, never as "nothing right now",
        // because the native side turns a short managed read into FFmpeg's own
        // end-of-file rather than letting it read as a stall.
        public int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            if (buffer.IsEmpty)
                return 0;

            fixed (byte* pointer = buffer)
            {
                var rc = FfmpegNative.Read(_handle, pointer, buffer.Length, out var written);
                if (rc == FfmpegNative.EndOfStream)
                    return 0;
                if (rc != FfmpegNative.Ok)
                    throw new FfmpegDecodeException("Decoding failed", rc);
                return written;
            }
        }

        // Returns where decode actually resumed, which is at or before the
        // request - the demuxer is keyframe-bound. Callers that report the
        // request instead end up with a scrubber permanently offset from the
        // audio; see ITrackDecoder.SeekSettled.
        public TimeSpan Seek(TimeSpan position)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);

            var requestedMs = (long)Math.Max(0, position.TotalMilliseconds);
            var rc = FfmpegNative.Seek(_handle, requestedMs, out var landedMs);
            if (rc != FfmpegNative.Ok)
                throw new FfmpegDecodeException($"Could not seek to {requestedMs}ms", rc);

            return TimeSpan.FromMilliseconds(landedMs);
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
                FfmpegNative.Close(handle);

            // After the native decoder is closed, and not before: it can be
            // inside a read callback right up to that call returning.
            if (_self.IsAllocated)
                _self.Free();

            if (_ownsStream)
                _stream?.Dispose();
        }

        private sealed class StreamState(Stream stream)
        {
            public Stream Stream { get; } = stream;
            // Latched so a stream that has already failed is not asked again
            // on every subsequent callback. The decode is over either way; the
            // difference is whether it ends or thrashes.
            public bool Broken;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadTrampoline(IntPtr opaque, byte* buffer, int bufferBytes)
        {
            var state = (StreamState?)GCHandle.FromIntPtr(opaque).Target;
            if (state is null || state.Broken || bufferBytes <= 0)
                return 0;

            try
            {
                return state.Stream.Read(new Span<byte>(buffer, bufferBytes));
            }
            catch (Exception)
            {
                // An exception must not cross back into C. The negative return
                // is the callback contract's own way to say the same thing,
                // and the native side turns it into an AVERROR the open or
                // read call surfaces.
                state.Broken = true;
                return -1;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static long SeekTrampoline(IntPtr opaque, long offset, int whence)
        {
            var state = (StreamState?)GCHandle.FromIntPtr(opaque).Target;
            if (state is null || state.Broken)
                return -1;

            try
            {
                if (whence == FfmpegNative.SeekSize)
                    return state.Stream.CanSeek ? state.Stream.Length : -1;

                return state.Stream.Seek(offset, (SeekOrigin)whence);
            }
            catch (Exception)
            {
                state.Broken = true;
                return -1;
            }
        }
    }
}
