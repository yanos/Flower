using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Audio;
using Flower.Audio.Ffmpeg;
using Flower.Models;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// A real decoder decoding a real HTTP stream, over a real socket.
//
// This is the layer SeekableHttpStreamTests cannot reach: that one proves the
// stream behaves like a file, this one proves the decoder is actually reading
// through it rather than opening the URL itself. The distinction is the whole
// change - a streamed track used to be fetched by whichever HTTP access module
// the platform had, which on iOS declared the stream unseekable and took an
// entire AAC album off the air. See SeekableHttpStream's header.
//
// Serving without range support is included deliberately: it is the shape that
// reproduced the original bug, and the decoder is expected to keep playing
// through it, because a forward-only read is all a WAV needs.
[Trait("Category", "RequiresFfmpeg")]
public class StreamedTrackDecodeTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _prefix;
    private byte[] _content = [];

    // Cuts every response body short at this offset, standing in for a
    // connection that goes away for good mid-track.
    private long? _cutBodyAt;

    public bool ServesRanges { get; set; } = true;

    public StreamedTrackDecodeTests()
    {

        // A port the OS picks, so concurrent test runs never collide.
        var port = FreePort();
        _prefix = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            try
            {
                Respond(context);
            }
            catch
            {
                // A client that hangs up mid-body is normal here: LibVLC
                // closes the stream the moment the decoder is retired.
            }
        }
    }

    private void Respond(HttpListenerContext context)
    {
        var response = context.Response;
        if (ServesRanges)
            response.Headers.Add("Accept-Ranges", "bytes");

        if (context.Request.HttpMethod == "HEAD")
        {
            response.ContentLength64 = _content.Length;
            response.Close();
            return;
        }

        var from = 0L;
        var header = context.Request.Headers["Range"];
        if (ServesRanges && header != null && header.StartsWith("bytes=", StringComparison.Ordinal))
        {
            var spec = header["bytes=".Length..].Split('-');
            _ = long.TryParse(spec[0], out from);
            response.StatusCode = 206;
            response.Headers.Add("Content-Range", $"bytes {from}-{_content.Length - 1}/{_content.Length}");
        }

        response.ContentLength64 = _content.Length - from;

        var count = _content.Length - (int)from;
        if (_cutBodyAt is { } cut)
        {
            response.OutputStream.Write(_content, (int)from, (int)Math.Max(0, Math.Min(count, cut - from)));
            response.OutputStream.Flush();
            response.Abort();
            return;
        }

        response.OutputStream.Write(_content, (int)from, count);
        response.Close();
    }

    public void Dispose()
    {
        _listener.Stop();
        ((IDisposable)_listener).Dispose();
    }

    // The catalog's own record of the track, as a real one would have it -
    // the duration matters, because a seek landing is reported relative to it.
    private Track Serve(TimeSpan duration, Func<int, short> sampleAt)
    {
        _content = SyntheticWav.Build(duration, sampleAt);
        return new Track
        {
            Title = "A streamed track",
            Path = _prefix + "rest/stream?id=abc",
            OriginFileExtension = "wav",
            Duration = duration,
        };
    }

    // Decodes until the ring stops filling, and answers how much PCM came out.
    private long DecodeFully(Track track)
    {
        var ring = new GaplessRingBuffer(4 * 1024 * 1024);
        using var decoder = new FfmpegTrackDecoder(track, ring, NullLogger<FfmpegTrackDecoder>.Instance);

        var drained = new ManualResetEventSlim();
        decoder.Drained += () => drained.Set();
        decoder.Faulted += () => drained.Set();

        Assert.Equal(DecodePrepareResult.Ready, decoder.PrepareAsync().GetAwaiter().GetResult());
        decoder.StartDecoding();
        Assert.True(drained.Wait(TimeSpan.FromSeconds(30)), "the decode never finished");

        return decoder.BytesProduced;
    }

    // One second of stereo S16 at the session rate, give or take the
    // resampler's edges.
    private static long ExpectedBytes() =>
        GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame;

    [Fact]
    public void A_streamed_track_decodes_through_Flowers_own_HTTP_stream()
    {
        var track = Serve(TimeSpan.FromSeconds(1), SyntheticWav.Marker(42));

        var produced = DecodeFully(track);

        Assert.InRange(produced, ExpectedBytes() / 2, ExpectedBytes() * 2);
    }

    // The shape that reproduced the original failure. A forward-only read is
    // enough for WAV, so this must still play - what it must not do is be
    // silently reported as a finished track having produced nothing.
    [Fact]
    public void A_server_that_refuses_ranges_still_decodes_forwards()
    {
        ServesRanges = false;
        var track = Serve(TimeSpan.FromSeconds(1), SyntheticWav.Marker(42));

        var produced = DecodeFully(track);

        Assert.InRange(produced, ExpectedBytes() / 2, ExpectedBytes() * 2);
    }

    // The headline. A streamed track that cannot be seeked is not a
    // degraded experience, it is an unplayable one for any container whose
    // demuxer needs to move around - which is what took the AAC album off the
    // air. Scrubbing has to work on a remote track exactly as on a local one.
    [Fact]
    public void A_streamed_track_can_be_seeked_mid_decode()
    {
        var track = Serve(TimeSpan.FromSeconds(10), SyntheticWav.Ramp());
        var ring = new GaplessRingBuffer(4 * 1024 * 1024);
        using var decoder = new FfmpegTrackDecoder(track, ring, NullLogger<FfmpegTrackDecoder>.Instance);

        var settled = new ManualResetEventSlim();
        var landedAt = -1L;
        decoder.SeekSettled += offset =>
        {
            landedAt = offset;
            settled.Set();
        };

        Assert.Equal(DecodePrepareResult.Ready, decoder.PrepareAsync().GetAwaiter().GetResult());
        decoder.StartDecoding();

        // Let some of the front of the track decode, so the seek is a real
        // move rather than a no-op at the start.
        Assert.True(SpinFor(() => decoder.BytesProduced > 0, TimeSpan.FromSeconds(15)), "the decode never started");

        decoder.Seek(0.5f);

        Assert.True(settled.Wait(TimeSpan.FromSeconds(15)), "the seek never landed");
        Assert.InRange(landedAt, ExpectedBytes() * 3, ExpectedBytes() * 7);
    }

    // An unreachable server has to be a distinguishable answer, because the
    // coordinator responds to it differently from an unplayable file - that is
    // what DecodePrepareResult exists for.
    [Fact]
    public void A_server_that_is_not_there_reports_a_failed_prepare()
    {
        var track = new Track
        {
            Title = "A track on a server that is gone",
            Path = $"http://127.0.0.1:{FreePort()}/rest/stream?id=abc",
            OriginFileExtension = "wav",
        };

        var ring = new GaplessRingBuffer(64 * 1024);
        using var decoder = new FfmpegTrackDecoder(track, ring, NullLogger<FfmpegTrackDecoder>.Instance);

        var prepared = decoder.PrepareAsync().GetAwaiter().GetResult();

        Assert.Contains(prepared, new[] { DecodePrepareResult.Failed, DecodePrepareResult.TimedOut });
    }


    // A stream that dies mid-track is a failed track, not a finished one.
    // LibVLC is deliberately handed a clean end of stream instead of an error
    // - it ignores the error and fabricates the missing tail, see
    // HttpMediaInput.Read - so the fault has to arrive by its own route, and
    // this is what proves it does. Without it the track would end quietly and
    // collect a play count for audio nobody heard.
    [Fact]
    public void A_stream_that_dies_mid_track_faults_rather_than_ending_quietly()
    {
        var track = Serve(TimeSpan.FromSeconds(10), SyntheticWav.Ramp());
        _cutBodyAt = _content.Length / 4;

        var ring = new GaplessRingBuffer(8 * 1024 * 1024);
        using var decoder = new FfmpegTrackDecoder(track, ring, NullLogger<FfmpegTrackDecoder>.Instance);

        var faulted = new ManualResetEventSlim();
        decoder.Faulted += () => faulted.Set();

        decoder.StartDecoding();

        Assert.True(faulted.Wait(TimeSpan.FromSeconds(30)), "a stream cut mid-track should fault");
        Assert.InRange(decoder.BytesProduced, 1, ExpectedBytes() * 9);
    }

    private static bool SpinFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(10);
        }

        return false;
    }
}
