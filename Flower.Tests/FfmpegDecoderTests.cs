using System;
using System.IO;
using System.Collections.Generic;

using Flower.Audio.Ffmpeg;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// Exercises the flower-ffmpeg façade end to end, against real FFmpeg
// libraries. Tagged like the LibVLC tests are: they need a native component
// that is built rather than restored (native/ffmpeg/macos/build.sh), so a
// machine without it filters them out instead of failing them.
//
// The first two tests are the whole argument for this decoder existing. Every
// other one is here so that argument keeps holding.
[Trait("Category", "RequiresFfmpeg")]
public class FfmpegDecoderTests : IDisposable
{
    private const int HiResRate = 96000;
    private const int Frames = 24000; // a quarter second at 96kHz

    private readonly string _directory = Directory.CreateTempSubdirectory("flower-ffmpeg").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string HiResFixture(string name = "hires.wav") =>
        SyntheticHiResWav.CreateFile(_directory, name, HiResRate, Frames, SyntheticHiResWav.Ramp24());

    private static byte[] DecodeAll(FfmpegDecoder decoder)
    {
        var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = decoder.Read(buffer)) > 0)
            output.Write(buffer, 0, read);
        return output.ToArray();
    }

    // The claim the whole façade rests on: a 24-bit source arrives with all
    // 24 bits. Measured against LibVLC's amem seam on 2026-09-03, the same
    // file comes back as 16-bit int regardless of the format requested.
    [Fact]
    public void A_24_bit_source_is_delivered_with_every_bit_intact()
    {
        using var decoder = FfmpegDecoder.OpenPath(HiResFixture(), FfmpegSampleFormat.S24);
        var pcm = DecodeAll(decoder);

        Assert.Equal(24, decoder.Format.SourceBitDepth);
        Assert.Equal(HiResRate, decoder.Format.SampleRate);
        Assert.Equal(Frames * 6, pcm.Length);

        var expected = SyntheticHiResWav.Ramp24();
        for (var frame = 0; frame < Frames; frame++)
        {
            for (var channel = 0; channel < 2; channel++)
            {
                var offset = frame * 6 + channel * 3;
                Assert.Equal(expected(frame), SyntheticHiResWav.ReadInt24(pcm.AsSpan(offset, 3)));
            }
        }
    }

    // The contrast, so the test above is not just asserting that arithmetic
    // works. Asking for S16 loses the low byte of every sample - which is
    // exactly and only what LibVLC can give, for every track, on every
    // platform.
    [Fact]
    public void The_same_source_asked_for_as_16_bit_loses_the_low_bits()
    {
        using var decoder = FfmpegDecoder.OpenPath(HiResFixture(), FfmpegSampleFormat.S16);
        var pcm = DecodeAll(decoder);

        Assert.Equal(Frames * 4, pcm.Length);

        var expected = SyntheticHiResWav.Ramp24();
        var differing = 0;
        for (var frame = 0; frame < Frames; frame++)
        {
            var delivered = BitConverter.ToInt16(pcm, frame * 4);
            if (delivered != (short)(expected(frame) >> 8))
                differing++;
        }

        // Rounding, not truncation, so a handful of samples land one step off
        // the naive shift; the point is that the low byte is gone either way.
        Assert.True(differing < Frames / 100, $"{differing} of {Frames} frames did not match a 16-bit truncation");
    }

    [Fact]
    public void The_source_format_is_reported_separately_from_the_delivered_one()
    {
        using var decoder = FfmpegDecoder.OpenPath(HiResFixture(), FfmpegSampleFormat.S16, sampleRate: 48000, channels: 2);

        Assert.Equal(HiResRate, decoder.Format.SourceSampleRate);
        Assert.Equal(24, decoder.Format.SourceBitDepth);
        Assert.Equal(48000, decoder.Format.SampleRate);
        Assert.Equal(FfmpegSampleFormat.S16, decoder.Format.SampleFormat);
        Assert.InRange(decoder.Format.Duration!.Value, TimeSpan.FromMilliseconds(245), TimeSpan.FromMilliseconds(255));
    }

    [Fact]
    public void Resampling_to_the_session_rate_halves_a_96kHz_source()
    {
        using var decoder = FfmpegDecoder.OpenPath(HiResFixture(), FfmpegSampleFormat.S16, sampleRate: 48000);
        var pcm = DecodeAll(decoder);

        // Not exactly half: swresample's filter has a delay, and the tail it
        // holds is flushed rather than dropped, so the count lands within a
        // few frames either side.
        Assert.InRange(pcm.Length / 4, Frames / 2 - 64, Frames / 2 + 64);
    }

    [Fact]
    public void A_stream_decodes_to_the_same_bytes_as_a_path()
    {
        var path = HiResFixture();
        using var fromPath = FfmpegDecoder.OpenPath(path, FfmpegSampleFormat.S24);
        var expected = DecodeAll(fromPath);

        using var source = new MemoryStream(File.ReadAllBytes(path));
        using var fromStream = FfmpegDecoder.OpenStream(source, FfmpegSampleFormat.S24);

        Assert.Equal(expected, DecodeAll(fromStream));
    }

    // The case that broke a whole AAC album on the phone: a stream the
    // platform would not let the demuxer seek. FFmpeg is told so up front and
    // reads it forwards instead of discarding the demuxer.
    [Fact]
    public void A_forward_only_stream_still_decodes()
    {
        var path = HiResFixture();
        using var fromPath = FfmpegDecoder.OpenPath(path, FfmpegSampleFormat.S24);
        var expected = DecodeAll(fromPath);

        using var source = new ForwardOnlyStream(File.ReadAllBytes(path));
        using var decoder = FfmpegDecoder.OpenStream(source, FfmpegSampleFormat.S24);

        Assert.Equal(expected, DecodeAll(decoder));
    }

    [Fact]
    public void A_forward_only_stream_refuses_to_seek()
    {
        using var source = new ForwardOnlyStream(File.ReadAllBytes(HiResFixture()));
        using var decoder = FfmpegDecoder.OpenStream(source, FfmpegSampleFormat.S24);

        Assert.Throws<FfmpegDecodeException>(() => decoder.Seek(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void Seeking_lands_at_or_before_the_request_and_says_where()
    {
        using var decoder = FfmpegDecoder.OpenPath(HiResFixture(), FfmpegSampleFormat.S24);

        var landed = decoder.Seek(TimeSpan.FromMilliseconds(100));
        Assert.InRange(landed, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));

        // PCM has no keyframes, so this particular container lands exactly;
        // the contract the caller has to honour is the range above.
        var remaining = DecodeAll(decoder).Length / 6;
        Assert.InRange(remaining, Frames - (int)(0.100 * HiResRate) - 64, Frames);
    }

    [Fact]
    public void Seeking_back_to_the_start_replays_the_same_samples()
    {
        using var decoder = FfmpegDecoder.OpenPath(HiResFixture(), FfmpegSampleFormat.S24);

        var first = new byte[6 * 512];
        Assert.Equal(first.Length, ReadFully(decoder, first));

        decoder.Seek(TimeSpan.Zero);

        var again = new byte[first.Length];
        Assert.Equal(again.Length, ReadFully(decoder, again));
        Assert.Equal(first, again);
    }

    [Fact]
    public void Reading_past_the_end_answers_zero_rather_than_repeating()
    {
        using var decoder = FfmpegDecoder.OpenPath(HiResFixture(), FfmpegSampleFormat.S24);
        DecodeAll(decoder);

        var buffer = new byte[4096];
        Assert.Equal(0, decoder.Read(buffer));
        Assert.Equal(0, decoder.Read(buffer));
    }

    [Fact]
    public void A_source_that_is_not_audio_fails_to_open()
    {
        var path = Path.Combine(_directory, "not-audio.wav");
        File.WriteAllText(path, "this is not a wav file, whatever its name says");

        Assert.Throws<FfmpegDecodeException>(() => FfmpegDecoder.OpenPath(path, FfmpegSampleFormat.S16));
    }

    [Fact]
    public void A_missing_file_fails_to_open_with_the_reason()
    {
        var exception = Assert.Throws<FfmpegDecodeException>(
            () => FfmpegDecoder.OpenPath(Path.Combine(_directory, "absent.wav"), FfmpegSampleFormat.S16));

        // FFmpeg's own diagnosis, not a flattened "could not open" - the
        // reason flower_error_string passes AVERROR codes through untouched.
        Assert.Contains("No such file", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // A stream that dies mid-track must fault, not spin and not end quietly.
    // Both halves matter. LibVLC's equivalent hot-looped 61,760 times on the
    // same signal and then had its demuxer fabricate the rest of the track,
    // so a cut-off song was indistinguishable from one the listener heard;
    // here the failure surfaces as an exception after a bounded number of
    // reads, with the audio that did arrive already handed over.
    [Fact]
    public void A_stream_that_fails_mid_track_faults_rather_than_ending_quietly()
    {
        var bytes = File.ReadAllBytes(HiResFixture());
        var source = new FailingStream(bytes, failAfter: bytes.Length / 3);
        using var decoder = FfmpegDecoder.OpenStream(source, FfmpegSampleFormat.S24);

        var produced = 0;
        var buffer = new byte[16384];
        var thrown = Assert.Throws<FfmpegDecodeException>(() =>
        {
            int read;
            while ((read = decoder.Read(buffer)) > 0)
                produced += read;
        });

        Assert.Contains("Input/output error", thrown.Message);
        Assert.InRange(produced, 1, Frames * 6 - 1);
        Assert.InRange(source.Reads, 1, 200);
    }

    [Fact]
    public void The_native_library_matches_the_abi_this_build_expects() =>
        // Not a tautology: it is the check that would have caught a façade
        // rebuilt with a reordered format struct, which otherwise reports
        // plausible nonsense rather than failing.
        FfmpegDecoder.OpenPath(HiResFixture(), FfmpegSampleFormat.S16).Dispose();

    private static int ReadFully(FfmpegDecoder decoder, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = decoder.Read(buffer[total..]);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var take = Math.Min(buffer.Length, bytes.Length - _position);
            if (take <= 0)
                return 0;
            bytes.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailingStream(byte[] bytes, int failAfter) : Stream
    {
        private int _position;

        public int Reads { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => _position = (int)value; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            if (_position >= failAfter)
                throw new IOException("the connection went away");

            var take = Math.Min(buffer.Length, bytes.Length - _position);
            if (take <= 0)
                return 0;
            bytes.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = (int)(origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => bytes.Length + offset,
            });
            return _position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
