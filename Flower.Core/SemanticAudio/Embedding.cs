using System;
using System.Buffers.Binary;
using System.Numerics.Tensors;

namespace Flower.SemanticAudio;

// Arithmetic on CLAP's 512-dimensional vectors. Every stored embedding is
// L2-normalised first, so a dot product is a cosine similarity.
public static class Embedding
{
    // An embedding as stored: 512 halves, little-endian, 1 KB. fp16 loses
    // nothing a ranking can see - the vectors are unit length and the scores
    // compared differ in the second decimal.
    public const int StoredBytes = Clap.Dimensions * 2;

    public static void Normalize(Span<float> vector)
    {
        var norm = TensorPrimitives.Norm(vector);
        if (norm > 0)
            TensorPrimitives.Divide(vector, norm, vector);
    }

    public static float[] Normalized(ReadOnlySpan<float> vector)
    {
        var copy = vector.ToArray();
        Normalize(copy);
        return copy;
    }

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => TensorPrimitives.Dot(a, b);

    // The normalised average of several unit vectors: a song's mean over the
    // clips the setting selects.
    public static float[] Mean(ReadOnlySpan<float[]> vectors)
    {
        var mean = new float[Clap.Dimensions];
        foreach (var vector in vectors)
            TensorPrimitives.Add(mean, vector, mean);
        Normalize(mean);
        return mean;
    }

    public static byte[] ToStored(ReadOnlySpan<float> vector)
    {
        if (vector.Length != Clap.Dimensions)
            throw new ArgumentException($"An embedding has {Clap.Dimensions} dimensions, not {vector.Length}.", nameof(vector));

        var bytes = new byte[StoredBytes];
        for (var i = 0; i < vector.Length; i++)
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(i * 2), (Half)vector[i]);
        return bytes;
    }

    public static float[] FromStored(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != StoredBytes)
            throw new ArgumentException($"A stored embedding is {StoredBytes} bytes, not {bytes.Length}.", nameof(bytes));

        var vector = new float[Clap.Dimensions];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)BinaryPrimitives.ReadHalfLittleEndian(bytes[(i * 2)..]);
        return vector;
    }
}
