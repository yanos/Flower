using System;
using System.Buffers.Binary;
using System.IO;

namespace Flower.Services;

// A picture's width and height, read from its header without decoding a
// pixel. Every place that needed the size used to decode the whole picture for
// it and throw the decode away - a 1400x1400 cover is ~7.8 MB of RGBA and a
// JPEG decompression, once per cover on a cold scroll, and twice more each time
// Track Info opened. The size is in the first few dozen bytes of a PNG or a
// WebP, and behind a short walk of a JPEG's segments.
//
// Null for anything else - another format, a truncated or malformed header -
// and the caller decodes to find out, as it always did. So a format missing
// here costs only what it cost before.
public static class ImageHeader
{
    public static (int Width, int Height)? TryReadSize(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return TryReadSize(stream);
    }

    // Leaves the stream where it found it. A stream that cannot seek is not
    // read at all, since a JPEG's segments are skipped rather than read.
    public static (int Width, int Height)? TryReadSize(Stream stream)
    {
        if (!stream.CanSeek)
            return null;

        var start = stream.Position;
        try
        {
            Span<byte> head = stackalloc byte[30];
            var length = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            head = head[..length];

            if (head is [0xFF, 0xD8, ..])
            {
                stream.Position = start + 2;
                return Valid(Jpeg(stream));
            }

            return Valid(Png(head) ?? WebP(head));
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            stream.Position = start;
        }
    }

    private static (int Width, int Height)? Valid((int Width, int Height)? size) =>
        size is { Width: > 0, Height: > 0 } ? size : null;

    // The signature, then IHDR - always the first chunk - with the width and
    // height at its start.
    private static (int, int)? Png(ReadOnlySpan<byte> head)
    {
        if (head.Length < 24
            || !head.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])
            || !head[12..16].SequenceEqual("IHDR"u8))
            return null;

        return (BinaryPrimitives.ReadInt32BigEndian(head[16..]), BinaryPrimitives.ReadInt32BigEndian(head[20..]));
    }

    // RIFF, WEBP, then the first chunk, which says which of three layouts the
    // size is in: lossy (VP8), lossless (VP8L), or extended (VP8X).
    private static (int, int)? WebP(ReadOnlySpan<byte> head)
    {
        if (head.Length < 30 || !head[..4].SequenceEqual("RIFF"u8) || !head[8..12].SequenceEqual("WEBP"u8))
            return null;

        var chunk = head[12..16];
        if (chunk.SequenceEqual("VP8 "u8) && head[23..26].SequenceEqual((ReadOnlySpan<byte>)[0x9D, 0x01, 0x2A]))
        {
            return (BinaryPrimitives.ReadUInt16LittleEndian(head[26..]) & 0x3FFF,
                BinaryPrimitives.ReadUInt16LittleEndian(head[28..]) & 0x3FFF);
        }

        if (chunk.SequenceEqual("VP8L"u8) && head[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(head[21..]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        if (chunk.SequenceEqual("VP8X"u8))
            return (UInt24(head[24..]) + 1, UInt24(head[27..]) + 1);

        return null;
    }

    private static int UInt24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;

    // Segment by segment to the first start-of-frame, which holds the size.
    // What comes before it can be long - an Exif block with its own thumbnail
    // is tens of kilobytes - so each segment is skipped by its length rather
    // than read.
    private static (int, int)? Jpeg(Stream stream)
    {
        Span<byte> field = stackalloc byte[5];
        while (true)
        {
            if (stream.ReadByte() != 0xFF)
                return null;

            // Any number of 0xFF may pad before a marker.
            int marker;
            do
            {
                marker = stream.ReadByte();
            }
            while (marker == 0xFF);

            switch (marker)
            {
                case < 0:
                    return null;
                // Markers that stand alone, with no length after them.
                case 0x01 or (>= 0xD0 and <= 0xD7):
                    continue;
                // The image data, or its end, before any frame: not a JPEG
                // this can read.
                case 0xD9 or 0xDA:
                    return null;
            }

            stream.ReadExactly(field[..2]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(field);
            if (length < 2)
                return null;

            // Start of frame, in any of its codings. C4, C8 and CC share the
            // range and are not frames (Huffman tables, a reserved marker,
            // arithmetic-coding conditioning).
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                // Precision, then height, then width.
                stream.ReadExactly(field);
                return (BinaryPrimitives.ReadUInt16BigEndian(field[3..]), BinaryPrimitives.ReadUInt16BigEndian(field[1..]));
            }

            stream.Seek(length - 2, SeekOrigin.Current);
            if (stream.Position >= stream.Length)
                return null;
        }
    }
}
