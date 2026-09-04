using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Flower.Audio;
using Flower.Audio.Ffmpeg;
using Flower.Models;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// FfmpegTrackDecoder driven the way GaplessCoordinator drives it: prepare,
// start, read the ring, seek, retire. Same fixtures and the same oracle as
// the LibVLC decoder's tests, because the two are interchangeable behind
// ITrackDecoder and the point is that they behave the same.
[Trait("Category", "RequiresFfmpeg")]
public class FfmpegTrackDecoderTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly string _directory = Directory.CreateTempSubdirectory("flower-ffmpeg-decoder").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private Track Fixture(TimeSpan duration, Func<int, short> sampleAt, string name = "fixture.wav")
    {
        var path = SyntheticWav.CreateFile(_directory, name, duration, sampleAt);
        return new Track { Title = name, Path = path, Duration = duration };
    }

    // The ring has to be drained while decode runs or the decoder parks on
    // backpressure, which is what it is supposed to do - so a test that wants
    // a whole track has to play the consumer's part.
    private static async Task<byte[]> DrainAsync(GaplessRingBuffer ring, ITrackDecoder decoder, long expectedBytes)
    {
        var output = new MemoryStream();
        var buffer = new byte[8192];
        var deadline = Stopwatch.StartNew();

        while (output.Length < expectedBytes && deadline.Elapsed < Patience)
        {
            var read = ring.Read(buffer);
            if (read > 0)
                output.Write(buffer, 0, read);
            else
                await Task.Delay(2);
        }

        return output.ToArray();
    }

    [Fact]
    public async Task A_local_track_decodes_to_the_bytes_the_fixture_wrote()
    {
        var duration = TimeSpan.FromSeconds(1);
        var track = Fixture(duration, SyntheticWav.Ramp());
        var expectedBytes = (long)duration.TotalSeconds * GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame;

        var ring = new GaplessRingBuffer(64 * 1024);
        using var decoder = new FfmpegTrackDecoder(track, ring);

        Assert.Equal(DecodePrepareResult.Ready, await decoder.PrepareAsync(TestContext.Current.CancellationToken));

        var drained = new TaskCompletionSource();
        decoder.Drained += () => drained.TrySetResult();
        decoder.Faulted += () => drained.TrySetException(new Exception("the decode faulted"));

        decoder.StartDecoding();
        var pcm = await DrainAsync(ring, decoder, expectedBytes);

        await drained.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(expectedBytes, pcm.Length);
        for (var frame = 0; frame < 4096; frame++)
            Assert.Equal(unchecked((short)frame), BitConverter.ToInt16(pcm, frame * GaplessFormat.BytesPerFrame));
    }

    [Fact]
    public async Task A_missing_file_prepares_as_failed_rather_than_throwing()
    {
        var track = new Track { Title = "gone", Path = Path.Combine(_directory, "absent.wav"), Duration = TimeSpan.FromSeconds(1) };
        using var decoder = new FfmpegTrackDecoder(track, new GaplessRingBuffer(64 * 1024));

        Assert.Equal(DecodePrepareResult.Failed, await decoder.PrepareAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_decoder_retired_before_it_prepares_says_so()
    {
        var track = Fixture(TimeSpan.FromSeconds(1), SyntheticWav.Ramp());
        using var decoder = new FfmpegTrackDecoder(track, new GaplessRingBuffer(64 * 1024));

        decoder.Retire();

        Assert.Equal(DecodePrepareResult.Retired, await decoder.PrepareAsync(TestContext.Current.CancellationToken));
    }

    // The seek reports where the demuxer actually landed, not where it was
    // asked to go, and the PCM that follows starts from there. A ramp fixture
    // is the oracle: the first sample after the seek says its own frame
    // number.
    [Fact]
    public async Task Seeking_resumes_from_where_it_landed_and_says_where_that_was()
    {
        var duration = TimeSpan.FromSeconds(10);
        var track = Fixture(duration, SyntheticWav.Ramp(), "ramp.wav");

        var ring = new GaplessRingBuffer(64 * 1024);
        using var decoder = new FfmpegTrackDecoder(track, ring);
        Assert.Equal(DecodePrepareResult.Ready, await decoder.PrepareAsync(TestContext.Current.CancellationToken));

        var settled = new TaskCompletionSource<long>();
        decoder.SeekSettled += bytes => settled.TrySetResult(bytes);

        decoder.StartDecoding();

        // Let it get going, so the seek is a real mid-decode reposition
        // rather than one applied before the first read.
        var buffer = new byte[8192];
        var started = Stopwatch.StartNew();
        while (decoder.BytesProduced == 0 && started.Elapsed < Patience)
        {
            ring.Read(buffer);
            await Task.Delay(2);
        }
        Assert.True(decoder.BytesProduced > 0, "decode never started");

        decoder.Seek(0.5f);

        var landedBytes = await settled.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var landedSeconds = landedBytes / (double)(GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);
        Assert.InRange(landedSeconds, 4.5, 5.0);

        // The first frame that arrives after the landing carries its own
        // index, so this is the seek verified in the audio rather than in the
        // decoder's own bookkeeping.
        var afterSeek = await ReadOneFrameAsync(ring);
        var expectedFrame = unchecked((short)(landedBytes / GaplessFormat.BytesPerFrame));
        Assert.InRange(Math.Abs(BitConverter.ToInt16(afterSeek) - expectedFrame), 0, 2048);
    }

    [Fact]
    public async Task Retiring_mid_decode_stops_the_thread()
    {
        var track = Fixture(TimeSpan.FromSeconds(30), SyntheticWav.Ramp(), "long.wav");

        var ring = new GaplessRingBuffer(64 * 1024);
        var decoder = new FfmpegTrackDecoder(track, ring);
        Assert.Equal(DecodePrepareResult.Ready, await decoder.PrepareAsync(TestContext.Current.CancellationToken));

        var faulted = false;
        decoder.Faulted += () => faulted = true;
        decoder.StartDecoding();

        var started = Stopwatch.StartNew();
        while (decoder.BytesProduced == 0 && started.Elapsed < Patience)
            await Task.Delay(2);

        // Deliberately without draining the ring: this retires a decoder that
        // is parked on backpressure, which is the case a retire flag alone
        // cannot get out of.
        decoder.Retire();

        var produced = decoder.BytesProduced;
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.Equal(produced, decoder.BytesProduced);
        Assert.False(faulted, "a retire is not a fault");
    }

    [Fact]
    public async Task Starting_without_a_prepare_faults_rather_than_decoding_nothing()
    {
        var track = Fixture(TimeSpan.FromSeconds(1), SyntheticWav.Ramp());
        using var decoder = new FfmpegTrackDecoder(track, new GaplessRingBuffer(64 * 1024));

        var faulted = new TaskCompletionSource();
        decoder.Faulted += () => faulted.TrySetResult();

        decoder.StartDecoding();

        await faulted.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("m4a", "mp4")]
    [InlineData("ALAC", "mp4")]
    [InlineData("mp3", "mp3")]
    [InlineData("flac", "flac")]
    [InlineData("ogg", null)]
    [InlineData(null, null)]
    public void The_catalogs_container_becomes_a_demuxer_name(string? suffix, string? expected) =>
        Assert.Equal(expected, FfmpegTrackDecoder.DemuxerHintFor(new Track
        {
            Title = "streamed",
            Path = "http://server:4533/rest/stream?id=abc",
            OriginFileExtension = suffix,
        }));

    private static async Task<byte[]> ReadOneFrameAsync(GaplessRingBuffer ring)
    {
        var frame = new byte[GaplessFormat.BytesPerFrame];
        var deadline = Stopwatch.StartNew();
        var read = 0;
        while (read < frame.Length && deadline.Elapsed < Patience)
        {
            var got = ring.Read(frame.AsSpan(read));
            if (got > 0)
                read += got;
            else
                await Task.Delay(2);
        }
        return frame;
    }
}
