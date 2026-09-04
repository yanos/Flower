using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Flower.Services;

// The framing for "give me the art for these albums" in one round trip,
// written once and read by both ends.
//
// Why a batch exists at all: an album grid asks for one cover per tile, and a
// library of any size has more albums than any per-source request budget can
// sensibly hold - 1400 covers against a 600/60s ceiling means a cold scroll
// spends the whole budget on pictures, and what gets refused next is
// /rest/stream. That was not a hypothetical: it is how an album stopped
// playing. Widening the ceiling until it stops hurting treats the symptom;
// asking once per screenful instead of once per tile removes the burst.
//
// Why a hand-rolled frame rather than JSON: the payload is a few dozen JPEGs.
// Base64 inside JSON would add a third to every byte of the one response shape
// in this app that is actually large, and multipart would need a MIME parser
// on a phone to read back what a for-loop can write. Length-prefixed blobs
// need neither.
//
//     int32 count
//     count x: int32 idBytes, id (UTF-8), int32 blobBytes, blob
//
// Little-endian throughout, and every requested id comes back whether or not
// the server found art for it: a zero-length blob is "this server has no
// picture for that album", which is a different answer from "the response was
// truncated", and the caller can tell them apart only because the ids are all
// present.
public static class CoverArtBatch
{
    // What one request may ask for. Both ends enforce it - the client so a
    // viewport full of tiles is split into whole requests rather than one
    // enormous one, the server because the request is attacker-shaped: a list
    // of ids is a list of file reads, and an unbounded list is an unbounded
    // read.
    public const int MaxIds = 32;

    // And what one response may carry. Album art is not small, and 32 covers
    // at a few hundred KB each is already the largest thing this server sends
    // that is not a track. Reaching it truncates rather than fails: the ids
    // that fit are useful now, and the caller asks again for the rest.
    public const int MaxBytes = 8 * 1024 * 1024;

    public const string ContentType = "application/x-flower-cover-art-batch";

    public static byte[] Write(IReadOnlyList<(string Id, byte[] Bytes)> entries)
    {
        using var buffer = new MemoryStream();
        Span<byte> scalar = stackalloc byte[4];

        BinaryPrimitives.WriteInt32LittleEndian(scalar, entries.Count);
        buffer.Write(scalar);

        foreach (var (id, bytes) in entries)
        {
            var idBytes = Encoding.UTF8.GetBytes(id);
            BinaryPrimitives.WriteInt32LittleEndian(scalar, idBytes.Length);
            buffer.Write(scalar);
            buffer.Write(idBytes);

            BinaryPrimitives.WriteInt32LittleEndian(scalar, bytes.Length);
            buffer.Write(scalar);
            buffer.Write(bytes);
        }

        return buffer.ToArray();
    }

    // Returns null rather than throwing on anything malformed. The caller is
    // painting album tiles: a response it cannot read means fall back to one
    // request per tile, which is a slow grid rather than a crash.
    public static Dictionary<string, byte[]>? Read(ReadOnlySpan<byte> frame)
    {
        var offset = 0;

        if (!TryReadInt32(frame, ref offset, out var count) || count < 0 || count > MaxIds)
            return null;

        var entries = new Dictionary<string, byte[]>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            if (!TryReadBlob(frame, ref offset, out var id))
                return null;
            if (!TryReadBlob(frame, ref offset, out var bytes))
                return null;

            entries[Encoding.UTF8.GetString(id)] = bytes.ToArray();
        }

        return entries;
    }

    private static bool TryReadInt32(ReadOnlySpan<byte> frame, ref int offset, out int value)
    {
        value = 0;
        if (offset + 4 > frame.Length)
            return false;

        value = BinaryPrimitives.ReadInt32LittleEndian(frame[offset..]);
        offset += 4;
        return true;
    }

    private static bool TryReadBlob(ReadOnlySpan<byte> frame, ref int offset, out ReadOnlySpan<byte> blob)
    {
        blob = default;
        // Widened deliberately: offset + length is an attacker-named addition,
        // and int.MaxValue as the length wraps it negative, which passes a
        // narrow bounds check and then slices past the end of the frame.
        if (!TryReadInt32(frame, ref offset, out var length) || length < 0 || (long)offset + length > frame.Length)
            return false;

        blob = frame.Slice(offset, length);
        offset += length;
        return true;
    }
}
