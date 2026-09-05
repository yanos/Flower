using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Flower.Audio;
using Flower.Audio.Ffmpeg;
using Flower.DeviceChecks;
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

    public void Dispose() => TempDirectory.DeleteWhenReleased(_directory);

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

    // Draining is not ending, and this is the difference.
    //
    // A track that fits its ring decodes in milliseconds and is then fully
    // buffered for its whole audible length - and the coordinator keeps the
    // decoder alive that whole time, because the tail still has to play out.
    // Every scrub during that window lands on a decoder that has already
    // reached the end of its input. The decode loop used to return there, so
    // the seek was accepted, stored, and read by nobody: the scrubber jumped
    // and the audio carried on regardless. For a short track that is not an
    // edge case, it is every seek of that track.
    //
    // The ring here is deliberately larger than the fixture, so the decode
    // finishes without anyone reading a byte - the same shape as decode-ahead
    // having filled the ring long before the track is over.
    [Fact]
    public async Task Seeking_after_the_track_has_fully_decoded_still_lands()
    {
        var duration = TimeSpan.FromSeconds(5);
        var track = Fixture(duration, SyntheticWav.Ramp(), "fully-decoded.wav");

        var ring = new GaplessRingBuffer(
            (int)(duration.TotalSeconds + 1) * (int)GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);
        using var decoder = new FfmpegTrackDecoder(track, ring);
        Assert.Equal(DecodePrepareResult.Ready, await decoder.PrepareAsync(TestContext.Current.CancellationToken));

        var drained = new TaskCompletionSource();
        decoder.Drained += () => drained.TrySetResult();

        var settled = new TaskCompletionSource<long>();
        decoder.SeekSettled += bytes => settled.TrySetResult(bytes);

        decoder.StartDecoding();

        await drained.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        decoder.Seek(0.5f);

        var landedBytes = await settled.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var landedSeconds = landedBytes / (double)(GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);
        Assert.InRange(landedSeconds, 2.0, 2.6);

        // And it is a live decoder afterwards, not just one that answered an
        // event: the audio from the new position has to arrive too. Nothing
        // needs clearing first - Seek resets the ring on the way in and
        // ApplySeek resets it again once the demuxer has moved, so everything
        // still in there was decoded from the landing point.
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

    // The shape GaplessCoordinator.Play uses. It calls PrepareAsync only when
    // decoding a track ahead of the one playing, so pressing play reaches
    // StartDecoding with nothing opened - and a decoder that faulted here
    // faulted on every press of play, while every check that prepared first
    // stayed green. This test previously asserted that fault, which was
    // mistaking the bug for the contract.
    [Fact]
    public async Task Starting_without_a_prepare_decodes_anyway()
    {
        var track = Fixture(TimeSpan.FromSeconds(1), SyntheticWav.Ramp());
        var ring = new GaplessRingBuffer(1024 * 1024);
        using var decoder = new FfmpegTrackDecoder(track, ring);

        var faulted = false;
        decoder.Faulted += () => faulted = true;

        decoder.StartDecoding();

        var started = Stopwatch.StartNew();
        while (decoder.BytesProduced == 0 && !faulted && started.Elapsed < Patience)
            await Task.Delay(2);

        Assert.False(faulted, "an unprepared start is the normal way to start");
        Assert.True(decoder.BytesProduced > 0, "no audio came out of an unprepared start");
    }

    [Fact]
    public async Task Starting_without_a_prepare_on_a_track_that_cannot_open_faults()
    {
        var track = new Track { Path = Path.Combine(Path.GetTempPath(), $"flower-missing-{Guid.NewGuid():N}.wav") };
        using var decoder = new FfmpegTrackDecoder(track, new GaplessRingBuffer(64 * 1024));

        var faulted = new TaskCompletionSource();
        decoder.Faulted += () => faulted.TrySetResult();

        decoder.StartDecoding();

        await faulted.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    // Only the MP4 family, and that narrowness is the point rather than an
    // omission. The suffix comes from the server's catalog and describes a
    // file on the server's disk, not the bytes on the wire; forcing a demuxer
    // discards FFmpeg's probe entirely, so a suffix that is wrong stops being
    // a slow open and becomes a track that will not play. MP4 keeps its hint
    // because there it buys something probing cannot: a moov atom at the end
    // of a stream nobody can seek. See FfmpegTrackDecoder.DemuxerHintFor, and
    // A_stream_whose_catalogued_container_is_wrong_still_opens below for what
    // happens when even that one is wrong.
    [Theory]
    [InlineData("m4a", "mp4")]
    [InlineData("ALAC", "mp4")]
    [InlineData("mp3", null)]
    [InlineData("flac", null)]
    [InlineData("wav", null)]
    [InlineData("ogg", null)]
    [InlineData(null, null)]
    public void The_catalogs_container_becomes_a_demuxer_name(string? suffix, string? expected) =>
        Assert.Equal(expected, FfmpegTrackDecoder.DemuxerHintFor(new Track
        {
            Title = "streamed",
            Path = "http://server:4533/rest/stream?id=abc",
            OriginFileExtension = suffix,
        }));

    // The other half of narrowing the hint: even the one container still
    // hinted has to survive being wrong about the bytes. A forced demuxer
    // that will not open the stream costs a rewind, not the track.
    [Fact]
    public void A_stream_whose_catalogued_container_is_wrong_still_opens()
    {
        var wav = SyntheticWav.Build(TimeSpan.FromSeconds(1), SyntheticWav.Ramp());
        using var stream = new MemoryStream(wav);

        using var decoder = FfmpegDecoder.OpenStream(
            stream, FfmpegSampleFormat.S16, formatHint: "mp4");

        // Opened by probing, so it found what the bytes actually are rather
        // than what the caller claimed they were.
        Assert.Equal(GaplessFormat.SampleRate, (uint)decoder.Format.SourceSampleRate);

        var buffer = new byte[4096];
        Assert.True(decoder.Read(buffer) > 0);
    }

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
