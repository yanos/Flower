using System;
using System.IO;
using System.Threading;

using Microsoft.Extensions.Logging;

using LibVLCSharp.Shared;

using Flower.Logging;
using Flower.Services;

namespace Flower.Audio
{
    // Hands LibVLC a remote track as a set of callbacks rather than a URL, so
    // the fetching is Flower's - see SeekableHttpStream for why that matters.
    //
    // LibVLCSharp ships StreamMediaInput, which wraps any Stream and would
    // otherwise do exactly this. It is not used because its constructor reads
    // Stream.CanSeek, and for this stream answering that question means asking
    // the server: constructing one therefore puts a blocking HTTP round trip
    // on whichever thread built the decoder, which for a track the user just
    // double-clicked is the UI thread. Every callback below is invoked by
    // LibVLC on its own reading thread, which is where that request belongs.
    //
    // CanSeek is declared true unconditionally rather than probed, for the
    // same reason. It is not a lie the caller can be hurt by: LibVLC treats a
    // seek callback returning false as a failed seek, which is precisely what
    // a server refusing ranges amounts to, and it is the honest answer at the
    // moment the question is actually being asked rather than a guess made
    // before any of it.
    public sealed class HttpMediaInput : MediaInput
    {
        private readonly SeekableHttpStream _stream;
        private readonly ILogger? _logger;
        private readonly string _describedAs;
        private int _failed;

        // Raised once, from LibVLC's own reading thread, when the stream has
        // stopped being readable and will not recover - see Read.
        public event Action? Failed;

        public HttpMediaInput(SeekableHttpStream stream, string path, ILogger? logger = null)
        {
            _stream = stream;
            _logger = logger;
            _describedAs = LogPath.Short(path);
            CanSeek = true;
        }

        // The first thing to touch the network for this track: reading Length
        // is what probes the server. Returning false here is how an
        // unreachable track is reported, and LibVLC turns it into the error
        // the decoder already knows how to handle.
        public override bool Open(out ulong size)
        {
            try
            {
                size = (ulong)_stream.Length;
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not open the stream for {Path}", _describedAs);
                size = 0;
                return false;
            }
        }

        // Returns 0 - a clean end of stream - when the fetching gives up, and
        // reports the failure out of band instead.
        //
        // The obvious answer is -1, which the callback contract documents as
        // an error. LibVLC 3.0.x does not act on it: handed -1 it called this
        // again immediately, and kept calling, 61,760 times in a two-second
        // test, while its WAV demuxer went on emitting the whole declared
        // length of a track only 30% of which had arrived. So -1 costs a hot
        // loop and buys a fabricated tail.
        //
        // 0 stops it dead, at the price of looking like a track that simply
        // ended - which would count as a play and hide the outage. Hence
        // Failed: LibVLC is told the stream is over, and the decoder is told
        // why, and those are answers to two different questions.
        public override unsafe int Read(IntPtr buffer, uint length)
        {
            try
            {
                return _stream.Read(new Span<byte>((void*)buffer, (int)length));
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _failed, 1) == 0)
                {
                    _logger?.LogWarning(ex, "Reading the stream for {Path} failed", _describedAs);
                    Failed?.Invoke();
                }

                return 0;
            }
        }

        public override bool Seek(ulong offset)
        {
            try
            {
                _stream.Seek((long)offset, SeekOrigin.Begin);
                return true;
            }
            catch (Exception ex)
            {
                // Expected, not exceptional, against a server that does not
                // serve ranges - and the one case where LibVLC's own answer to
                // a refused seek (give up on the demuxer) is the right one.
                _logger?.LogDebug(ex, "The stream for {Path} could not seek to {Offset}", _describedAs, offset);
                return false;
            }
        }

        // LibVLC opens and closes the same media across a stop/play cycle, so
        // this rewinds rather than disposing: the stream outlives the input and
        // is released by TrackDecoder.Retire alongside the native Media.
        public override void Close()
        {
            try
            {
                if (_stream.CanSeek)
                    _stream.Seek(0, SeekOrigin.Begin);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Rewinding the stream for {Path} on close failed", _describedAs);
            }
        }
    }
}
