using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Flower.Tests.TestSupport;

// Builds a real, decodable PNG of an exact pixel size in memory - the image
// equivalent of SyntheticWav, and for the same reason: AlbumArtLoader's
// decode-scaling behaviour is about the *intrinsic* size of the art, so its
// tests need images of chosen sizes without checking binary fixtures into the
// repo. Solid-colour 8-bit RGB, no interlacing, one IDAT.
public static class SyntheticPng
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Build(int width, int height, byte red = 0xC0, byte green = 0x40, byte blue = 0x80)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            raw[row] = 0; // filter type: none
            for (var x = 0; x < width; x++)
            {
                raw[row + 1 + x * 3] = red;
                raw[row + 2 + x * 3] = green;
                raw[row + 3 + x * 3] = blue;
            }
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type: truecolour
        // [10..12] stay 0: deflate compression, adaptive filtering, no interlace.

        using var png = new MemoryStream();
        png.Write(Signature);
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", Deflate(raw));
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    // PNG's IDAT payload is a zlib stream, not a bare deflate one.
    private static byte[] Deflate(byte[] data)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] payload)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(payload);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes, payload));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] type, byte[] payload)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            crc = Step(crc, b);
        }

        foreach (var b in payload)
        {
            crc = Step(crc, b);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Step(uint crc, byte b)
    {
        crc ^= b;
        for (var i = 0; i < 8; i++)
        {
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return crc;
    }
}
