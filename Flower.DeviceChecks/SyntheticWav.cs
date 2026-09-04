using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

using Flower.Audio;

namespace Flower.DeviceChecks;

// Generates minimal, valid PCM WAV files at GaplessFormat's own sample
// rate/channel count, so LibVLC never needs to resample/channel-mix them -
// what TrackDecoder produces should be byte-identical to what's written
// here, which is what makes these fixtures useful as an exact oracle rather
// than "it didn't crash".
//
// The core API takes a per-frame sample generator so tests can compose
// whatever payload they need (both stereo channels get the same value per
// frame): Marker() for a constant value - cheap to tell "is this chunk of
// rendered PCM from track A or track B" at a splice/seek point - and Ramp()
// for a monotonically increasing counter - lets a test verify local
// frame-to-frame continuity (no dropped/duplicated frames) without caring
// about the absolute Int16 wraparound point.
public static class SyntheticWav
{
    private const int HeaderSize = 44;

    public static Func<int, short> Marker(byte value) => _ => value;

    public static Func<int, short> Ramp() => frame => unchecked((short)frame);

    public static string CreateFile(string directory, string fileName, TimeSpan duration, Func<int, short> sampleAt)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, Build(duration, sampleAt));
        return path;
    }

    public static byte[] Build(TimeSpan duration, Func<int, short> sampleAt)
    {
        var frameCount = (int)(duration.TotalSeconds * GaplessFormat.SampleRate);
        var dataSize = frameCount * GaplessFormat.BytesPerFrame;
        var buffer = new byte[HeaderSize + dataSize];

        WriteHeader(buffer, dataSize);
        FillData(buffer.AsSpan(HeaderSize), frameCount, sampleAt);

        return buffer;
    }

    private static void WriteHeader(byte[] buffer, int dataSize)
    {
        var span = buffer.AsSpan();

        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataSize);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(span[8..]);

        Encoding.ASCII.GetBytes("fmt ").CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16); // PCM fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // AudioFormat = PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)GaplessFormat.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], (int)GaplessFormat.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], (int)GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)GaplessFormat.BytesPerFrame); // block align
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], (short)(GaplessFormat.BytesPerSample * 8)); // bits per sample

        Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataSize);
    }

    private static void FillData(Span<byte> data, int frameCount, Func<int, short> sampleAt)
    {
        for (var frame = 0; frame < frameCount; frame++)
        {
            var sample = sampleAt(frame);
            var frameSpan = data.Slice(frame * GaplessFormat.BytesPerFrame, GaplessFormat.BytesPerFrame);
            BinaryPrimitives.WriteInt16LittleEndian(frameSpan[..2], sample);
            BinaryPrimitives.WriteInt16LittleEndian(frameSpan[2..4], sample);
        }
    }
}
