using System;
using System.Runtime.InteropServices;

using FFAudio;

namespace Flower.SemanticAudio;

// Where the analyzer gets a song's grid windows from. The seam is for tests:
// the analyzer's logic runs against made-up songs, and FfmpegClipSource is
// held to Python's decoding by the reference tests.
public interface IClipSource
{
    // Whether this host can decode at all. False where FFAudio.NET has no
    // native library for the platform (an arm64 Linux server, today): the
    // engine then says so once, rather than recording every song as one it
    // couldn't hear.
    bool IsAvailable => true;

    IClipReader Open(string path);
}

public interface IClipReader : IDisposable
{
    // The song's length as its container states it, or null when it doesn't.
    TimeSpan? Duration { get; }

    // Window k's samples, 48 kHz mono float: 10 s, or less for the last one,
    // and empty when the song ends before it starts. Windows are asked for in
    // ascending order.
    float[] ReadWindow(int window);
}

public sealed class FfmpegClipSource : IClipSource
{
    public bool IsAvailable => Decoder.IsAvailable;

    public IClipReader Open(string path) => new FfmpegClipReader(Decoder.OpenPath(path, SampleFormat.F32, Clap.SampleRate, 1));
}

// One song's decoder, read window by window (docs/VIBE-PLAYLIST-PLAN.md,
// "Decoding"), decoding straight through from the start and throwing away
// what lies between the windows asked for.
//
// Never by seeking, though six clips of a four-minute song are a minute of
// it. Measured on 60 songs of the real library (November 2026), a window
// reached by a seek came out up to 48 ms early in AAC and MP3 alike - the
// encoder's delay, which a seek's landing doesn't account for - and that cost
// embeddings down to cosine 0.925 against Python's, where decoding through
// gave 0.9987 or better on every clip. Decoding a whole song took 0.30 s on
// an M1 Max (900 times real time), against 1.2 s of inference for its six
// clips, so exactness costs about a quarter more time per song.
internal sealed class FfmpegClipReader(Decoder decoder) : IClipReader
{
    private readonly byte[] _scratch = new byte[64 * 1024];

    // The index of the next sample Read returns.
    private long _position;
    private bool _ended;

    public TimeSpan? Duration => decoder.Format.Duration;

    public float[] ReadWindow(int window)
    {
        var start = (long)window * Clap.ClipSamples;
        if (start < _position)
            throw new InvalidOperationException($"Window {window} is behind the decoder; windows are read in ascending order.");

        if (!Skip(start - _position))
            return [];

        var samples = new float[Clap.ClipSamples];
        var read = Fill(MemoryMarshal.AsBytes(samples.AsSpan())) / sizeof(float);
        _position += read;
        return read == samples.Length ? samples : samples.AsSpan(0, read).ToArray();
    }

    private bool Skip(long samples)
    {
        var bytes = samples * sizeof(float);
        while (bytes > 0)
        {
            var chunk = (int)Math.Min(bytes, _scratch.Length);
            var read = Fill(_scratch.AsSpan(0, chunk));
            if (read == 0)
                return false;

            bytes -= read;
            _position += read / sizeof(float);
        }

        return !_ended;
    }

    // Reads until the buffer is full or the song ends; returns the bytes read.
    private int Fill(Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length && !_ended)
        {
            var read = decoder.Read(buffer[total..]);
            if (read == 0)
                _ended = true;
            total += read;
        }

        return total;
    }

    public void Dispose() => decoder.Dispose();
}
