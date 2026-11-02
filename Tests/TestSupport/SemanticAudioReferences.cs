using System;
using System.IO;
using System.IO.Compression;

namespace Flower.Tests.TestSupport;

// The files scripts/semantic-audio/make-references.py writes for the Vibe
// Playlist engine's tests, embedded in whichever test assembly holds them
// (see Flower.Tests.csproj) under SemanticAudio.References.<file name>.
public static class SemanticAudioReferences
{
    public static Stream Open(string name)
    {
        var assembly = typeof(SemanticAudioReferences).Assembly;
        return assembly.GetManifestResourceStream("SemanticAudio.References." + name)
            ?? throw new InvalidOperationException($"{name} is not embedded in {assembly.GetName().Name}; run scripts/semantic-audio/make-references.py.");
    }

    public static byte[] Read(string name)
    {
        using var stream = Open(name);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static byte[] ReadGzipped(string name)
    {
        using var stream = new GZipStream(Open(name), CompressionMode.Decompress);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
